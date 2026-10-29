using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace DesktopLauncher.UI;

/// <summary>托盘图标 + 菜单（打开启动器 / 悬浮球 / 自启 / 设置 / 退出）。</summary>
public class TrayController : IDisposable
{
    private static readonly string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "DesktopLauncher";

    private readonly App _app;
    private WinForms.NotifyIcon? _notify;

    public TrayController(App app)
    {
        _app = app;
        var menu = new WinForms.ContextMenuStrip();

        var open = new WinForms.ToolStripMenuItem($"打开启动器 ({app.ConfigStore.Config.HotkeyModifiers}+{app.ConfigStore.Config.HotkeyKey})");
        open.Click += (_, _) => _app.ToggleLauncher();

        var ball = new WinForms.ToolStripMenuItem("显示悬浮球") { CheckOnClick = true, Checked = app.ConfigStore.Config.BallVisible };
        ball.Click += (_, _) =>
        {
            if (ball.Checked) _app.ShowBall(); else _app.HideBall();
        };

        var hideIcons = new WinForms.ToolStripMenuItem("隐藏桌面应用图标");
        hideIcons.Click += (_, _) =>
        {
            var hiding = !_app.ConfigStore.Config.HideDesktopIconsEnabled;
            _app.Tray.ShowBubble(hiding ? "桌面整理" : "恢复图标", _app.SetDesktopIconsHidden(hiding));
        };

        var auto = new WinForms.ToolStripMenuItem("开机自动启动") { CheckOnClick = true, Checked = IsAutoStartEnabled() };
        auto.Click += (_, _) => SetAutoStart(auto.Checked);

        var settings = new WinForms.ToolStripMenuItem("设置…");
        settings.Click += (_, _) => _app.Launcher.ShowSettings();

        var exit = new WinForms.ToolStripMenuItem("退出");
        exit.Click += (_, _) => _app.Shutdown();

        menu.Items.AddRange([open, new WinForms.ToolStripSeparator(), ball, hideIcons, auto, settings, new WinForms.ToolStripSeparator(), exit]);

        // 每次打开菜单时按当前状态刷新文字
        menu.Opening += (_, _) =>
        {
            var enabled = _app.ConfigStore.Config.HideDesktopIconsEnabled;
            hideIcons.Text = enabled ? "恢复桌面应用图标" : "隐藏桌面应用图标";
        };

        _notify = new WinForms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "桌面启动器",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notify.DoubleClick += (_, _) => _app.ToggleLauncher();
    }

    public void ShowBubble(string title, string message, int timeoutMs = 5000)
    {
        if (_notify == null) return;
        _notify.BalloonTipTitle = title;
        _notify.BalloonTipText = message;
        _notify.ShowBalloonTip(timeoutMs);
    }

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunName) is string v && !string.IsNullOrEmpty(v);
        }
        catch { return false; }
    }

    public static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null) return;
            if (enable)
            {
                var exe = Environment.ProcessPath ?? "";
                if (!string.IsNullOrEmpty(exe)) key.SetValue(RunName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunName, throwOnMissingValue: false);
            }
        }
        catch { /* 无权限或被策略限制时静默失败，可在设置里看到状态 */ }
    }

    /// <summary>GDI+ 画一个圆角渐变 + 白色宫格的托盘图标。</summary>
    private static Icon CreateTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            path.AddEllipse(1, 1, 30, 30);
            using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                new Rectangle(0, 0, 32, 32), Color.FromArgb(120, 140, 255), Color.FromArgb(70, 90, 220), 45f);
            g.FillPath(brush, path);
            using var white = new SolidBrush(Color.White);
            g.FillEllipse(white, 8, 8, 6, 6);
            g.FillEllipse(white, 18, 8, 6, 6);
            g.FillEllipse(white, 8, 18, 6, 6);
            g.FillEllipse(white, 18, 18, 6, 6);
        }
        var hIcon = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        if (_notify != null)
        {
            _notify.Visible = false;
            _notify.Dispose();
            _notify = null;
        }
    }
}
