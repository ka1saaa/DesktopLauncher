using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopLauncher.Models;
using DesktopLauncher.Services;
using Microsoft.Win32;

namespace DesktopLauncher.UI.Dialogs;

/// <summary>添加/移除应用：桌面候选与已添加应用统一列表——未添加的可勾选多选，已添加的显示「移除」按钮。</summary>
public partial class AddAppDialog : Window
{
    private static readonly Brush RowNormal = Brushes.Transparent;
    private static readonly Brush RowChecked = new SolidColorBrush(Color.FromArgb(0x2E, 0x5B, 0x8C, 0xFF));
    private static readonly Brush RowHover = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

    private readonly List<AppEntry> _candidates = new();
    private readonly HashSet<AppEntry> _checked = new();
    private readonly HashSet<string> _inLauncher = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<AppEntry, Border> _rows = new();

    /// <summary>用户勾选/浏览选定的应用（仅未添加的）。</summary>
    public List<AppEntry> SelectedApps => _checked.ToList();

    /// <summary>本次会话里从启动器移除的应用 Id。</summary>
    public HashSet<string> RemovedIds { get; } = new();

    public AddAppDialog()
    {
        InitializeComponent();
        RowChecked.Freeze();
        RowHover.Freeze();

        var cfg = App.Instance.ConfigStore.Config;
        foreach (var a in cfg.Apps)
            _inLauncher.Add(a.Path);

        // 统一列表：启动器已有的应用 + 桌面扫描出的新候选（按路径去重，已有条目优先）
        _candidates.AddRange(cfg.Apps);
        var scanned = new AppScanner().Scan(cfg.ExtraScanDirs);
        _candidates.AddRange(scanned.Where(s => !_inLauncher.Contains(s.Path)));

        RebuildList("");
    }

    private void RebuildList(string filter)
    {
        ListPanel.Children.Clear();
        _rows.Clear();

        var hasFilter = !string.IsNullOrEmpty(filter);
        foreach (var app in _candidates)
        {
            if (hasFilter &&
                !app.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !app.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            var row = BuildRow(app);
            _rows[app] = row;
            ListPanel.Children.Add(row);
        }

        if (ListPanel.Children.Count == 0)
        {
            ListPanel.Children.Add(new TextBlock
            {
                Text = hasFilter ? $"没有匹配「{filter}」的应用" : "没有扫描到可添加的应用，试试「浏览…」",
                Foreground = (Brush)FindResource("SubTextBrush"),
                FontSize = 13,
                Margin = new Thickness(8, 16, 8, 16),
            });
        }
        UpdateCount();
    }

    private Border BuildRow(AppEntry app)
    {
        bool inLauncher = _inLauncher.Contains(app.Path);

        var img = new Image
        {
            Width = 28,
            Height = 28,
            Source = App.Instance.Icons.GetIcon(app.Path, 28),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };

        var name = new TextBlock
        {
            Text = app.Name,
            Foreground = (Brush)FindResource("TextBrush"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var path = new TextBlock
        {
            Text = app.Path,
            Foreground = (Brush)FindResource("SubTextBrush"),
            FontSize = 11,
            Opacity = 0.8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 300,
            ToolTip = app.Path,
        };

        var panel = new DockPanel { LastChildFill = true };
        var info = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(img);
        info.Children.Add(name);
        info.Children.Add(path);

        var row = new Border
        {
            Height = 46,
            CornerRadius = new CornerRadius(8),
            Background = RowNormal,
            Cursor = Cursors.Hand,
            Tag = app,
        };

        if (inLauncher)
        {
            // 已添加：左侧「已添加」标签，右侧「移除」按钮（仅按钮可点）
            var tagLabel = new TextBlock
            {
                Text = "已添加",
                Foreground = (Brush)FindResource("SubTextBrush"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            var removeBtn = new Button
            {
                Content = "移除",
                Width = 56,
                Height = 28,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7A)),
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0x7A, 0x7A)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
            };
            removeBtn.Click += (_, e) =>
            {
                e.Handled = true;
                RemoveFromLauncher(app);
            };

            DockPanel.SetDock(tagLabel, Dock.Left);
            DockPanel.SetDock(removeBtn, Dock.Right);
            panel.Children.Add(removeBtn);
            panel.Children.Add(tagLabel);
            panel.Children.Add(info);
            row.Cursor = Cursors.Arrow; // 行本身不可点，只有移除按钮
        }
        else
        {
            // 未添加：勾选框，点整行切换
            var box = new CheckBox
            {
                IsChecked = _checked.Contains(app),
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 2, 0),
            };
            info.Children.Insert(0, box);
            panel.Children.Add(info);

            row.Background = _checked.Contains(app) ? RowChecked : RowNormal;
            row.MouseEnter += (_, _) => { if (!_checked.Contains(app)) row.Background = RowHover; };
            row.MouseLeave += (_, _) => row.Background = _checked.Contains(app) ? RowChecked : RowNormal;
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                Toggle(app, row, box);
            };
        }

        row.Child = panel;
        return row;
    }

    /// <summary>从启动器移除应用（仅删配置条目，不动任何文件）。</summary>
    private void RemoveFromLauncher(AppEntry app)
    {
        var cfg = App.Instance.ConfigStore.Config;
        var entry = cfg.Apps.FirstOrDefault(a => a.Id == app.Id || string.Equals(a.Path, app.Path, StringComparison.OrdinalIgnoreCase));
        if (entry != null)
        {
            cfg.Apps.Remove(entry);
            foreach (var cat in cfg.Categories)
                cat.AppIds.Remove(entry.Id);
            RemovedIds.Add(entry.Id);
        }
        _inLauncher.Remove(app.Path);
        _checked.Remove(app);
        App.Instance.ConfigStore.Save();

        // 该行变为可添加状态
        RebuildList(FilterBox.Text.Trim());
    }

    private void Toggle(AppEntry app, Border row, CheckBox box)
    {
        if (_checked.Contains(app))
        {
            _checked.Remove(app);
            box.IsChecked = false;
            row.Background = RowHover;
        }
        else
        {
            _checked.Add(app);
            box.IsChecked = true;
            row.Background = RowChecked;
        }
        UpdateCount();
    }

    private void UpdateCount()
    {
        CountText.Text = _checked.Count == 0 ? "已选 0 项" : $"已选 {_checked.Count} 项";
        OkButton.IsEnabled = _checked.Count > 0;
        OkButton.Opacity = _checked.Count > 0 ? 1 : 0.5;
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => RebuildList(FilterBox.Text.Trim());

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择程序或快捷方式（可多选）",
            Filter = "程序与快捷方式|*.exe;*.lnk;*.url|所有文件|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        foreach (var file in dialog.FileNames)
        {
            if (_inLauncher.Contains(file)) continue;
            var app = _candidates.FirstOrDefault(a => string.Equals(a.Path, file, StringComparison.OrdinalIgnoreCase));
            if (app == null)
            {
                app = new AppEntry { Name = Path.GetFileNameWithoutExtension(file), Path = file };
                _candidates.Add(app);
            }
            _checked.Add(app); // 浏览选中的默认勾上
        }

        FilterBox.Clear(); // 显示全部并让新项可见
        RebuildList("");
        ScrollToChecked();
    }

    private void ScrollToChecked()
    {
        var first = _rows.FirstOrDefault(kv => _checked.Contains(kv.Key)).Value;
        first?.BringIntoView();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (SelectedApps.Count == 0)
        {
            MessageBox.Show(this, "请先勾选至少一个应用。", "添加应用");
            return;
        }
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
