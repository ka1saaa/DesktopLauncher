using System.IO;

namespace DesktopLauncher.Services;

public record ShortcutInfo(string TargetPath, string Arguments, string WorkingDirectory, string IconLocation);

/// <summary>解析 .lnk（WScript.Shell COM，无需引用 IWshRuntimeLibrary）与 .url 快捷方式。</summary>
public static class LnkParser
{
    public static ShortcutInfo Parse(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (ext == ".lnk")
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic sc = shell.CreateShortcut(path);
                var info = new ShortcutInfo(
                    (string)sc.TargetPath,
                    (string)sc.Arguments,
                    (string)sc.WorkingDirectory,
                    (string)sc.IconLocation);
                return info;
            }
            if (ext == ".url")
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                        return new ShortcutInfo(line[4..].Trim(), "", "", "");
                }
            }
        }
        catch
        {
            // 解析失败按无目标处理（显示置灰图标）。
        }
        return new ShortcutInfo("", "", "", "");
    }
}
