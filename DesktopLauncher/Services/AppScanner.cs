using System.IO;
using DesktopLauncher.Models;

namespace DesktopLauncher.Services;

/// <summary>扫描桌面（重定向感知）及自定义目录里的 .lnk / .exe / .url，生成候选应用列表。</summary>
public class AppScanner
{
    private static readonly string[] Extensions = [".lnk", ".exe", ".url"];

    public List<AppEntry> Scan(IEnumerable<string> extraDirs)
    {
        var dirs = new List<string> { DesktopPathResolver.GetUserDesktop() };
        var common = DesktopPathResolver.GetCommonDesktop();
        if (!dirs.Contains(common, StringComparer.OrdinalIgnoreCase))
            dirs.Add(common);
        foreach (var d in extraDirs.Where(Directory.Exists))
            if (!dirs.Contains(d, StringComparer.OrdinalIgnoreCase))
                dirs.Add(d);

        var result = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in dirs)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch { continue; }

            foreach (var file in files)
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (!Extensions.Contains(ext)) continue;

                var name = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (name.StartsWith("~$", StringComparison.Ordinal) || name.Equals("desktop", StringComparison.OrdinalIgnoreCase) && ext == ".ini") continue;

                // 同名去重：用户桌面优先于公共桌面。
                if (seen.Contains(name)) continue;
                seen.Add(name);
                result.Add(new AppEntry { Name = name, Path = file });
            }
        }
        return result.OrderBy(r => r.Name, StringComparer.CurrentCulture).ToList();
    }
}
