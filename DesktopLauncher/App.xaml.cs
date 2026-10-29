using System.Threading;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopLauncher.Services;
using DesktopLauncher.UI;

namespace DesktopLauncher;

public partial class App : Application
{
    private Mutex? _mutex;
    private static bool _singletonOwned;

    public static App Instance => (App)Current;

    public ConfigStore ConfigStore { get; private set; } = null!;
    public IconService Icons { get; private set; } = null!;
    public HotkeyService Hotkeys { get; private set; } = null!;
    public TrayController Tray { get; private set; } = null!;
    public LauncherWindow Launcher { get; private set; } = null!;
    public FloatingBallWindow? Ball { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, @"Local\DesktopLauncher_SingleInstance", out _singletonOwned);
        if (!_singletonOwned)
        {
            MessageBox.Show("桌面启动器已经在运行了（请查看任务栏托盘或桌面悬浮球）。",
                "桌面启动器", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        ConfigStore = new ConfigStore();
        Icons = new IconService();

        // 调试：--icontest=path 把提取出的图标存 PNG，验证图标链路
        var icontest = e.Args.FirstOrDefault(a => a.StartsWith("--icontest=", StringComparison.OrdinalIgnoreCase));
        if (icontest != null)
        {
            var path = icontest["--icontest=".Length..];
            var src = Icons.GetIcon(path, 48);
            var outPath = System.IO.Path.Combine(ConfigStore.AppDataDir, "test_icon.png");
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
            using (var fs = System.IO.File.Create(outPath))
                enc.Save(fs);
            System.IO.File.WriteAllText(System.IO.Path.Combine(ConfigStore.AppDataDir, "test_diag.txt"),
                IconService.Diagnose(path) + IconService.CountPixels(src, "GetIcon-result"));
            Shutdown();
            return;
        }
        Launcher = new LauncherWindow();
        Tray = new TrayController(this);

        if (ConfigStore.Config.BallVisible)
            ShowBall();

        Hotkeys = new HotkeyService();
        Hotkeys.HotkeyPressed += ToggleLauncher;
        Hotkeys.FolderPressed += OpenFolder;
        var ok = Hotkeys.Start(ConfigStore.Config.HotkeyModifiers, ConfigStore.Config.HotkeyKey);
        if (!ok)
            Tray.ShowBubble("全局热键注册失败",
                $"热键 {ConfigStore.Config.HotkeyModifiers}+{ConfigStore.Config.HotkeyKey} 可能被其他程序占用，请在设置中更换。");
        var failedFolders = Hotkeys.RegisterFolders(ConfigStore.Config.FolderHotkeys);
        if (failedFolders.Count > 0)
            Tray.ShowBubble("文件夹快捷键注册失败", string.Join("\n", failedFolders.Take(3)));

        PreloadIcons();

        // 调试/自启动直开：--show 启动后立即打开启动器；--popup=<类别Id> 同时打开该文件夹面板；--addapp 直接弹添加应用对话框
        var hideArg = e.Args.FirstOrDefault(a => a is "--hideicons" or "--showicons");
        if (hideArg != null)
        {
            var result = SetDesktopIconsHidden(hideArg == "--hideicons");
            System.IO.File.WriteAllText(System.IO.Path.Combine(ConfigStore.AppDataDir, "hide_result.txt"), result);
            Shutdown();
            return;
        }
        // 诊断：--shellmenu=<路径> 检查原生 Shell 菜单 COM 链路到哪一步失败
        var shellArg = e.Args.FirstOrDefault(a => a.StartsWith("--shellmenu=", StringComparison.OrdinalIgnoreCase));
        if (shellArg != null)
        {
            Launcher.OpenOnScreenWithMouse();
            var path = shellArg["--shellmenu=".Length..];
            var hwnd = new System.Windows.Interop.WindowInteropHelper(Launcher).Handle;
            var log = Services.ShellContextMenu.Diagnose(hwnd, path);
            System.IO.File.WriteAllText(System.IO.Path.Combine(ConfigStore.AppDataDir, "shell_diag.txt"), log);
            Shutdown();
            return;
        }
        if (e.Args.Contains("--addapp", StringComparer.OrdinalIgnoreCase))
        {
            Launcher.OpenOnScreenWithMouse();
            var dialog = new UI.Dialogs.AddAppDialog { Owner = Launcher };
            dialog.Show();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            timer.Tick += (_, _) => Shutdown();
            timer.Start();
            return;
        }
        if (e.Args.Contains("--dockball", StringComparer.OrdinalIgnoreCase))
        {
            Ball?.DebugDock(UI.FloatingBallWindow.Edge.Right);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (_, _) => Shutdown();
            timer.Start();
            return;
        }
        if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
        {
            Launcher.OpenOnScreenWithMouse();
            var dialog = new UI.Dialogs.SettingsDialog { Owner = Launcher };
            dialog.Show();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                try
                {
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        (int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(dialog);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using var fs = System.IO.File.Create(System.IO.Path.Combine(ConfigStore.AppDataDir, "settings_dump.png"));
                    enc.Save(fs);
                }
                catch { /* dump 失败不影响 */ }
                Shutdown();
            };
            timer.Start();
            return;
        }
        if (e.Args.Contains("--show", StringComparer.OrdinalIgnoreCase) || e.Args.Any(a => a.StartsWith("--popup", StringComparison.OrdinalIgnoreCase)))
        {
            Launcher.OpenOnScreenWithMouse();
            var popupArg = e.Args.FirstOrDefault(a => a.StartsWith("--popup", StringComparison.OrdinalIgnoreCase));
            if (popupArg != null)
            {
                var catId = popupArg.Contains('=') ? popupArg["--popup=".Length..] : "";
                if (e.Args.Contains("--edit", StringComparer.OrdinalIgnoreCase))
                {
                    var cat = ConfigStore.Config.Categories.FirstOrDefault(c => c.AppIds.Count > 0)
                              ?? ConfigStore.Config.Categories.FirstOrDefault();
                    if (cat != null) Launcher.OpenFolderPopupSelectDebug(cat.Id, 2);

                    // 程序内自渲染导出，验证 UI 不受用户操作干扰
                    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    t.Tick += (_, _) =>
                    {
                        try
                        {
                            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                                (int)Launcher.Width, (int)Launcher.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                            rtb.Render(Launcher);
                            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                            using var fs = System.IO.File.Create(System.IO.Path.Combine(ConfigStore.AppDataDir, "edit_dump.png"));
                            enc.Save(fs);
                        }
                        catch { /* dump 失败不影响 */ }
                        Shutdown();
                    };
                    t.Start();
                }
                else
                {
                    var cat = Launcher.HasCategory(catId) ?? ConfigStore.Config.Categories.FirstOrDefault(c => c.AppIds.Count > 0);
                    if (cat != null) Launcher.OpenFolderPopupById(cat.Id);
                }
            }
        }
    }

    public void ShowBall()
    {
        if (Ball == null)
        {
            Ball = new FloatingBallWindow();
            Ball.Show();
        }
        else
        {
            Ball.Show();
        }
        ConfigStore.Config.BallVisible = true;
        ConfigStore.Save();
    }

    public void HideBall()
    {
        Ball?.Hide();
        ConfigStore.Config.BallVisible = false;
        ConfigStore.Save();
    }

    public void ToggleLauncher()
    {
        if (Launcher.IsVisible) Launcher.CloseLauncher();
        else Launcher.OpenOnScreenWithMouse();
    }

    /// <summary>隐藏/恢复主桌面应用图标，返回给用户看的结果文案。</summary>
    public string SetDesktopIconsHidden(bool hide)
    {
        var cfg = ConfigStore.Config;
        string message;
        if (hide)
        {
            var r = Services.DesktopIconHider.Hide(cfg);
            cfg.HideDesktopIconsEnabled = true;
            message = r.HiddenCount > 0
                ? $"已隐藏 {r.HiddenCount} 个应用图标（文件夹与压缩包未动）"
                : "桌面没有新的应用图标需要隐藏";
            if (r.SkippedNoPermission > 0)
                message += $"；{r.SkippedNoPermission} 个公共桌面图标因权限不足跳过";
            if (r.HiddenCount > 0 && Services.ExplorerSettings.GetShowHiddenFiles() != Services.ExplorerSettings.ShowHidden)
                message += "；已同步将资源管理器切换为「不显示隐藏的文件」，恢复图标时会还原";
        }
        else
        {
            var r = Services.DesktopIconHider.Restore(cfg);
            cfg.HideDesktopIconsEnabled = false;
            message = r.RestoredCount > 0
                ? $"已恢复 {r.RestoredCount} 个应用图标显示"
                : "没有已隐藏的应用图标";
        }
        ConfigStore.Save();
        return message;
    }

    /// <summary>重新注册热键（设置里更换后调用）。</summary>
    public bool ApplyHotkey(string modifiers, string key)
    {
        Hotkeys.Stop();
        var ok = Hotkeys.Start(modifiers, key);
        if (ok)
        {
            ConfigStore.Config.HotkeyModifiers = modifiers;
            ConfigStore.Config.HotkeyKey = key;
            ConfigStore.Save();
        }
        return ok;
    }

    /// <summary>设置里文件夹快捷键变更后调用：保存并重注册，返回失败的组合。</summary>
    public List<string> ApplyFolderHotkeys()
    {
        ConfigStore.Save();
        var failed = Hotkeys.RegisterFolders(ConfigStore.Config.FolderHotkeys);
        return failed;
    }

    private static void OpenFolder(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
        }
        catch
        {
            // 目录被删/移动时静默忽略
        }
    }

    private void PreloadIcons()
    {
        var paths = ConfigStore.Config.Apps.Select(a => a.Path).ToList();
        var t = new Thread(() => Icons.Preload(paths))
        {
            IsBackground = true,
            Name = "IconPreload",
        };
        t.SetApartmentState(ApartmentState.STA); // WScript.Shell COM 解析 .lnk 需要 STA
        t.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ConfigStore.Save();
        Hotkeys?.Dispose();
        Tray?.Dispose();
        _mutex?.ReleaseMutex();
        base.OnExit(e);
    }
}

/// <summary>应用路径 → 图标 BitmapSource。</summary>
public class IconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is string path && !string.IsNullOrEmpty(path))
        {
            var size = 48;
            if (parameter is string ps && int.TryParse(ps, out var s)) size = s;
            return App.Instance.Icons.GetIcon(path, size);
        }
        return IconService.FallbackIcon(48);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => Binding.DoNothing;
}
