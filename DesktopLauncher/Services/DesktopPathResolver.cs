using System.IO;
using Microsoft.Win32;

namespace DesktopLauncher.Services;

/// <summary>
/// 解析用户真实桌面路径。很多用户会把桌面文件夹重定向到其他盘或 OneDrive，
/// 因此优先读注册表 User Shell Folders（REG_EXPAND_SZ），绝不能写死 C:\Users\xxx\Desktop。
/// </summary>
public static class DesktopPathResolver
{
    private const string UserShellFoldersKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";

    /// <summary>当前用户桌面（重定向感知）。失败时回退 GetFolderPath。</summary>
    public static string GetUserDesktop()
    {
        var path = ReadShellFolder("Desktop");
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            return path!;

        var fallback = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return string.IsNullOrEmpty(fallback) ? @"C:\Users\Public\Desktop" : fallback;
    }

    /// <summary>公共（所有用户）桌面。</summary>
    public static string GetCommonDesktop()
    {
        var path = ReadShellFolder("Common Desktop", hive: Registry.LocalMachine);
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            return path!;

        var fallback = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        return string.IsNullOrEmpty(fallback) ? GetUserDesktop() : fallback;
    }

    private static string? ReadShellFolder(string valueName, RegistryKey? hive = null)
    {
        try
        {
        using var key = (hive ?? Registry.CurrentUser).OpenSubKey(UserShellFoldersKey);
            if (key?.GetValue(valueName) is string raw)
            {
                var expanded = Environment.ExpandEnvironmentVariables(raw);
                return Path.GetFullPath(expanded);
            }
        }
        catch
        {
            // 注册表读取失败走回退路径。
        }
        return null;
    }
}
