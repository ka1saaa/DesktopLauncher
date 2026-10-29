using System.IO;
using DesktopLauncher.Models;

namespace DesktopLauncher.Services;

public record IconHideResult(int HiddenCount, int SkippedNoPermission, int AlreadyHidden);
public record IconRestoreResult(int RestoredCount, int MissingCount);

/// <summary>
/// 隐藏/恢复主桌面的应用图标：仅对桌面上的 .lnk/.exe/.url 设置 Windows Hidden 属性。
/// 文件夹、压缩包及其他所有类型文件一律不动；只改属性，绝不删除移动。
/// </summary>
public static class DesktopIconHider
{
    private static readonly string[] HideExtensions = [".lnk", ".exe", ".url"];

    public static IconHideResult Hide(AppConfig cfg)
    {
        // 资源管理器开着"显示隐藏文件"时，Hidden 属性只会让图标淡化而非消失；
        // 切到"不显示"并记住原值，恢复图标时还原用户的资源管理器设置。
        if (ExplorerSettings.GetShowHiddenFiles() == ExplorerSettings.ShowHidden)
        {
            cfg.ExplorerShowHiddenOriginal = ExplorerSettings.ShowHidden;
            ExplorerSettings.SetShowHiddenFiles(ExplorerSettings.DontShowHidden);
        }

        int hidden = 0, denied = 0, already = 0;
        var known = cfg.HiddenIconPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in GetDesktopDirs())
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch { continue; }

            foreach (var file in files)
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (!HideExtensions.Contains(ext)) continue;

                try
                {
                    var attr = File.GetAttributes(file);
                    if (attr.HasFlag(FileAttributes.Hidden))
                    {
                        already++;
                        if (!known.Contains(file)) cfg.HiddenIconPaths.Add(file); // 用户手动隐藏过的也记账，恢复时一并处理
                        continue;
                    }
                    File.SetAttributes(file, attr | FileAttributes.Hidden);
                    cfg.HiddenIconPaths.Add(file);
                    hidden++;
                }
                catch
                {
                    denied++; // 公共桌面等无权限场景
                }
            }
        }
        return new IconHideResult(hidden, denied, already);
    }

    public static IconRestoreResult Restore(AppConfig cfg)
    {
        // 还原资源管理器"显示隐藏文件"的原设置（若隐藏期间改动过）
        if (cfg.ExplorerShowHiddenOriginal is int original)
        {
            ExplorerSettings.SetShowHiddenFiles(original);
            cfg.ExplorerShowHiddenOriginal = null;
        }

        int restored = 0, missing = 0;
        var stillHidden = new List<string>();

        foreach (var file in cfg.HiddenIconPaths)
        {
            try
            {
                if (!File.Exists(file))
                {
                    missing++; // 文件已被删除/移走，从清单清理
                    continue;
                }
                var attr = File.GetAttributes(file);
                if (attr.HasFlag(FileAttributes.Hidden))
                {
                    File.SetAttributes(file, attr & ~FileAttributes.Hidden);
                    restored++;
                    // 已恢复，不再留在清单
                }
                else
                {
                    restored++; // 属性已是非隐藏（用户自行改过），视为完成
                }
            }
            catch
            {
                stillHidden.Add(file); // 无权限等失败：保留在清单，下次再试
            }
        }
        cfg.HiddenIconPaths = stillHidden;
        return new IconRestoreResult(restored, missing);
    }

    private static IEnumerable<string> GetDesktopDirs()
    {
        yield return DesktopPathResolver.GetUserDesktop();
        var common = DesktopPathResolver.GetCommonDesktop();
        if (!string.Equals(common, DesktopPathResolver.GetUserDesktop(), StringComparison.OrdinalIgnoreCase))
            yield return common;
    }
}
