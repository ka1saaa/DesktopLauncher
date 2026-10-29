using System.Diagnostics;
using System.IO;
using DesktopLauncher.Models;

namespace DesktopLauncher.Services;

/// <summary>启动应用：.lnk/.url 走 Shell，.exe 可带参数与工作目录。</summary>
public static class LaunchService
{
    public static bool Launch(AppEntry app, out string error)
    {
        try
        {
            if (!File.Exists(app.Path))
            {
                error = $"找不到文件：{app.Path}";
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = app.Path,
                UseShellExecute = true,
            };
            var ext = Path.GetExtension(app.Path).ToLowerInvariant();
            if (ext == ".exe")
            {
                psi.Arguments = app.Arguments ?? "";
                if (!string.IsNullOrEmpty(app.WorkingDirectory) && Directory.Exists(app.WorkingDirectory))
                    psi.WorkingDirectory = app.WorkingDirectory;
            }
            using var _ = Process.Start(psi);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
