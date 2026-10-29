using System.Windows;
using System.Windows.Controls;
using DesktopLauncher.Models;
using DesktopLauncher.Services;
using WinForms = System.Windows.Forms;

namespace DesktopLauncher.UI.Dialogs;

public partial class SettingsDialog : Window
{
    private static readonly string[] ModifierOptions = ["Ctrl+Alt", "Ctrl+Shift", "Alt+Shift", "Ctrl+Alt+Shift", "Win+Alt"];
    private static readonly string[] KeyOptions =
        [
            "A","B","C","D","E","F","G","H","I","J","K","L","M","N","O","P","Q","R","S","T","U","V","W","X","Y","Z",
            "1","2","3","4","5","6","7","8","9","0","SPACE",
        ];

    public SettingsDialog()
    {
        InitializeComponent();

        ModifierBox.ItemsSource = ModifierOptions;
        KeyBox.ItemsSource = KeyOptions;

        var cfg = App.Instance.ConfigStore.Config;
        ModifierBox.SelectedItem = ModifierOptions.Contains(cfg.HotkeyModifiers) ? cfg.HotkeyModifiers : ModifierOptions[0];
        KeyBox.SelectedItem = KeyOptions.Contains(cfg.HotkeyKey.ToUpperInvariant()) ? cfg.HotkeyKey.ToUpperInvariant() : "D";
        HotkeyHint.Text = $"当前：{cfg.HotkeyModifiers}+{cfg.HotkeyKey}　按热键可随时打开/收起启动器";

        AutoStartBox.IsChecked = TrayController.IsAutoStartEnabled();
        BallVisibleBox.IsChecked = cfg.BallVisible;
        CloseOnLaunchBox.IsChecked = cfg.CloseOnLaunch;
        HideIconsBox.IsChecked = cfg.HideDesktopIconsEnabled || cfg.HiddenIconPaths.Count > 0;

        foreach (var dir in cfg.ExtraScanDirs) DirsList.Items.Add(dir);
        RebuildFolderHotkeyRows();

        AutoStartBox.Checked += (_, _) => TrayController.SetAutoStart(true);
        AutoStartBox.Unchecked += (_, _) => TrayController.SetAutoStart(false);
        BallVisibleBox.Checked += (_, _) => { App.Instance.ShowBall(); Save(); };
        BallVisibleBox.Unchecked += (_, _) => { App.Instance.HideBall(); Save(); };
        CloseOnLaunchBox.Checked += (_, _) => { cfg.CloseOnLaunch = true; Save(); };
        CloseOnLaunchBox.Unchecked += (_, _) => { cfg.CloseOnLaunch = false; Save(); };
        HideIconsBox.Checked += (_, _) => HideIconsResult.Text = App.Instance.SetDesktopIconsHidden(true);
        HideIconsBox.Unchecked += (_, _) => HideIconsResult.Text = App.Instance.SetDesktopIconsHidden(false);
    }

    private void OnApplyHotkey(object sender, RoutedEventArgs e)
    {
        if (ModifierBox.SelectedItem is not string mods || KeyBox.SelectedItem is not string key)
        {
            HotkeyHint.Text = "请先选择热键组合";
            return;
        }
        if (App.Instance.ApplyHotkey(mods, key))
        {
            HotkeyHint.Text = $"已更新为 {mods}+{key}";
            HotkeyHint.Foreground = System.Windows.Media.Brushes.LightGreen;
        }
        else
        {
            HotkeyHint.Text = $"{mods}+{key} 注册失败（可能被占用），请换一组";
            HotkeyHint.Foreground = System.Windows.Media.Brushes.OrangeRed;
        }
    }

    // ---------- 文件夹快捷键 ----------

    private void RebuildFolderHotkeyRows()
    {
        FolderHotkeyList.Children.Clear();
        foreach (var hk in App.Instance.ConfigStore.Config.FolderHotkeys)
            FolderHotkeyList.Children.Add(BuildFolderHotkeyRow(hk));
    }

    private Border BuildFolderHotkeyRow(FolderHotkey hk)
    {
        var pathBtn = new Button
        {
            Content = System.IO.Path.GetFileName(hk.Path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : hk.Path,
            ToolTip = hk.Path,
            Width = 220,
            Height = 32,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x40)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
            FontSize = 13,
        };
        pathBtn.Click += (_, _) =>
        {
            var dialog = new WinForms.FolderBrowserDialog { Description = "选择要快捷打开的文件夹" };
            if (dialog.ShowDialog() != WinForms.DialogResult.OK) return;
            hk.Path = dialog.SelectedPath;
            Save();
            RebuildFolderHotkeyRows();
            ApplyAndReport();
        };

        var modBox = new ComboBox
        {
            ItemsSource = ModifierOptions,
            SelectedItem = ModifierOptions.Contains(hk.Modifiers) ? hk.Modifiers : ModifierOptions[0],
            Width = 130,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var keyBox = new ComboBox
        {
            ItemsSource = KeyOptions,
            SelectedItem = KeyOptions.Contains(hk.Key.ToUpperInvariant()) ? hk.Key.ToUpperInvariant() : null,
            Width = 84,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        modBox.SelectionChanged += (_, _) => { hk.Modifiers = (string)modBox.SelectedItem; Save(); ApplyAndReport(); };
        keyBox.SelectionChanged += (_, _) => { hk.Key = keyBox.SelectedItem as string ?? ""; Save(); ApplyAndReport(); };

        var remove = new Button
        {
            Content = "×",
            Width = 30,
            Height = 30,
            Margin = new Thickness(8, 0, 0, 0),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 0xFF, 0x7A, 0x7A)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
        };
        remove.Click += (_, _) =>
        {
            App.Instance.ConfigStore.Config.FolderHotkeys.Remove(hk);
            Save();
            RebuildFolderHotkeyRows();
            ApplyAndReport();
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(pathBtn);
        row.Children.Add(modBox);
        row.Children.Add(keyBox);
        row.Children.Add(remove);

        return new Border { Child = row };
    }

    private void OnAddFolderHotkey(object sender, RoutedEventArgs e)
    {
        var dialog = new WinForms.FolderBrowserDialog { Description = "选择要快捷打开的文件夹" };
        if (dialog.ShowDialog() != WinForms.DialogResult.OK) return;

        var cfg = App.Instance.ConfigStore.Config;
        cfg.FolderHotkeys.Add(new FolderHotkey
        {
            Path = dialog.SelectedPath,
            Modifiers = "Ctrl+Alt",
            Key = NextFreeKey(cfg),
        });
        Save();
        RebuildFolderHotkeyRows();
        ApplyAndReport();
    }

    /// <summary>为新增行挑一个未使用的键位。</summary>
    private static string NextFreeKey(AppConfig cfg)
    {
        var used = cfg.FolderHotkeys.Select(h => h.Key.ToUpperInvariant()).ToHashSet();
        used.Add(cfg.HotkeyKey.ToUpperInvariant());
        foreach (var k in KeyOptions)
            if (!used.Contains(k))
                return k;
        return "";
    }

    private void ApplyAndReport()
    {
        var failed = App.Instance.ApplyFolderHotkeys();
        FolderHotkeyResult.Text = failed.Count == 0 ? "" : "注册失败（可能被占用）：" + string.Join("；", failed);
    }

    // ---------- 额外扫描目录 ----------

    private void OnAddDir(object sender, RoutedEventArgs e)
    {
        var dialog = new WinForms.FolderBrowserDialog { Description = "选择要加入扫描的目录" };
        if (dialog.ShowDialog() != WinForms.DialogResult.OK) return;
        var path = dialog.SelectedPath;
        if (DirsList.Items.Cast<string>().Any(d => string.Equals(d, path, StringComparison.OrdinalIgnoreCase)))
            return;
        DirsList.Items.Add(path);
        App.Instance.ConfigStore.Config.ExtraScanDirs.Add(path);
        Save();
    }

    private void OnRemoveDir(object sender, RoutedEventArgs e)
    {
        if (DirsList.SelectedItem is not string path) return;
        DirsList.Items.Remove(path);
        App.Instance.ConfigStore.Config.ExtraScanDirs.RemoveAll(
            d => string.Equals(d, path, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();

    private void Save() => App.Instance.ConfigStore.Save();
}
