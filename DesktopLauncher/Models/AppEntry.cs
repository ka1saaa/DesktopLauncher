namespace DesktopLauncher.Models;

/// <summary>启动器中的一个应用条目（指向 .lnk / .exe / .url，只记录路径，不移动文件）。</summary>
public class AppEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>启动路径：.lnk / .exe / .url 的完整路径。</summary>
    public string Path { get; set; } = "";
    /// <summary>仅 .exe 直启时可能带的参数。</summary>
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }

    public AppEntry Clone() => new()
    {
        Id = Id,
        Name = Name,
        Path = Path,
        Arguments = Arguments,
        WorkingDirectory = WorkingDirectory,
    };
}

/// <summary>分类文件夹。</summary>
public class Category
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>该类别包含的应用 Id，有序。</summary>
    public List<string> AppIds { get; set; } = new();
}

/// <summary>文件夹快捷键：按下即在资源管理器打开指定文件夹。</summary>
public class FolderHotkey
{
    public string Path { get; set; } = "";
    public string Modifiers { get; set; } = "Ctrl+Alt";
    public string Key { get; set; } = "";
}

/// <summary>持久化配置（%APPDATA%\DesktopLauncher\config.json）。</summary>
public class AppConfig
{
    public List<Category> Categories { get; set; } = new();
    public List<AppEntry> Apps { get; set; } = new();

    /// <summary>悬浮球位置（屏幕坐标，DIP）；-1 表示使用默认位置（屏幕右侧边缘中部）。</summary>
    public double BallX { get; set; } = -1;
    public double BallY { get; set; } = -1;
    public bool BallVisible { get; set; } = true;

    /// <summary>全局热键，默认 Ctrl+Alt+D。</summary>
    public string HotkeyModifiers { get; set; } = "Ctrl+Alt";
    public string HotkeyKey { get; set; } = "D";

    /// <summary>启动应用后是否自动关闭启动器。</summary>
    public bool CloseOnLaunch { get; set; } = true;

    /// <summary>额外扫描目录（重定向旧桌面、下载目录等）。</summary>
    public List<string> ExtraScanDirs { get; set; } = new();

    /// <summary>文件夹快捷键列表（按下即用资源管理器打开）。</summary>
    public List<FolderHotkey> FolderHotkeys { get; set; } = new();

    /// <summary>桌面图标隐藏开关（只隐藏应用类文件，文件夹/压缩包不动）。</summary>
    public bool HideDesktopIconsEnabled { get; set; }

    /// <summary>已被隐藏属性隐藏的桌面应用文件清单（用于一键恢复）。</summary>
    public List<string> HiddenIconPaths { get; set; } = new();

    /// <summary>开启图标隐藏时资源管理器"显示隐藏文件"的原设置（恢复时还原；null=未改动过）。</summary>
    public int? ExplorerShowHiddenOriginal { get; set; }

    private static readonly string[] DefaultCategories = ["常用", "游戏", "办公", "工具"];

    public static AppConfig CreateDefault()
    {
        var cfg = new AppConfig();
        cfg.Categories.AddRange(DefaultCategories.Select(n => new Category { Name = n }));
        return cfg;
    }

    public AppEntry? FindApp(string id) => Apps.FirstOrDefault(a => a.Id == id);

    public Category? FindCategory(string id) => Categories.FirstOrDefault(c => c.Id == id);

    public Category? FindCategoryByName(string name) =>
        Categories.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}
