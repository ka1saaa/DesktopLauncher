using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopLauncher.Models;
using DesktopLauncher.Services;
using DesktopLauncher.UI.Dialogs;
using System.Windows.Controls.Primitives;
using WinForms = System.Windows.Forms;

namespace DesktopLauncher.UI;

/// <summary>全屏启动器：文件夹宫格 + 展开面板 + 搜索 + 编辑模式。</summary>
public partial class LauncherWindow : Window
{
    private const string DragFormat = "DesktopLauncherAppId";

    private bool _editMode;
    private bool _popupSelectMode; // 文件夹面板内的选择模式（批量删除/移动，不依赖编辑模式）
    private bool _dialogOpen; // 弹窗期间不因失焦自动关闭
    private bool _wasActivated; // 只有被激活过的窗口，失焦才自动关闭
    private Category? _openCategory;
    private readonly HashSet<string> _popupSelected = new(); // 选择模式下点选的应用（批量操作）
    private System.Windows.Point _dragStart; // 磁贴拖拽起点（区分点击与拖动）
    private bool _wasDragging;
    private DispatcherTimer? _toastTimer;
    private DispatcherTimer? _autoCloseTimer;

    /// <summary>编辑模式或面板选择模式中——此时点击磁贴=选中，磁贴带单删按钮。</summary>
    private bool SelectActive => _editMode || _popupSelectMode;

    private AppConfig Cfg => App.Instance.ConfigStore.Config;

    public LauncherWindow()
    {
        InitializeComponent();
        Activated += (_, _) =>
        {
            _wasActivated = true;
            _autoCloseTimer?.Stop();
        };
    }

    // ---------- 打开 / 关闭 ----------

    /// <summary>在鼠标（悬浮球/热键触发点）所在屏幕全屏打开。</summary>
    public void OpenOnScreenWithMouse()
    {
        _wasActivated = false;
        _autoCloseTimer?.Stop();
        var mouse = WinForms.Cursor.Position; // 物理像素
        var screen = WinForms.Screen.FromPoint(mouse);

        // 先按系统估算的 DPI 近似落位，把窗口放到目标屏幕上
        double scale = GetDpiScale();
        Left = screen.Bounds.Left / scale;
        Top = screen.Bounds.Top / scale;
        Width = screen.Bounds.Width / scale;
        Height = screen.Bounds.Height / scale;

        Show();

        // 落位后读窗口所在屏幕的实际 DPI（PerMonitorV2 下可能与估算不同），精确重算全屏尺寸
        var actual = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11;
        if (actual is > 0.01 && Math.Abs(actual.Value - scale) > 0.01)
        {
            Left = screen.Bounds.Left / actual.Value;
            Top = screen.Bounds.Top / actual.Value;
            Width = screen.Bounds.Width / actual.Value;
            Height = screen.Bounds.Height / actual.Value;
        }

        WallpaperImage.Source = WallpaperService.GetBlurredWallpaper(new Size(Width, Height));

        RebuildFolders();
        Activate();
        TrySetForeground();
        // 悬浮球保持在启动器之上（可见可点，再点一次即收起），不抢焦点
        App.Instance.Ball?.RaiseToTop();

        // 淡入 + 宫格轻微上浮
        Opacity = 0;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160));
        BeginAnimation(OpacityProperty, fade);
        FoldersPanel.RenderTransform = new TranslateTransform();
        var rise = new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new QuadraticEase() };
        FoldersPanel.RenderTransform.BeginAnimation(TranslateTransform.YProperty, rise);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>热键/悬浮球触发时进程拥有前台权限，再拉一次确保启动器获得焦点。</summary>
    private void TrySetForeground()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) SetForegroundWindow(handle);
    }

    private static double GetDpiScale()
    {
        try
        {
            using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
            return g.DpiX / 96.0;
        }
        catch { return 1.0; }
    }

    public void CloseLauncher()
    {
        _autoCloseTimer?.Stop();
        _openCategory = null;
        PopupOverlay.Visibility = Visibility.Collapsed;
        SearchBox.Clear();
        Hide();
    }

    private void OnDeactivated(object sender, EventArgs e)
    {
        // 仅当窗口被激活过才在失焦时自动收起；后台启动拿不到焦点的场景保持显示等用户点击。
        if (!IsVisible || _dialogOpen || !_wasActivated) return;

        _autoCloseTimer?.Stop();
        _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _autoCloseTimer.Tick += (_, _) =>
        {
            _autoCloseTimer!.Stop();
            if (IsVisible && !_dialogOpen && !IsActive) CloseLauncher();
        };
        _autoCloseTimer.Start();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (PopupOverlay.Visibility == Visibility.Visible)
            {
                _openCategory = null;
                PopupOverlay.Visibility = Visibility.Collapsed;
            }
            else
            {
                CloseLauncher();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && _openCategory != null)
        {
            RenameCategory(_openCategory);
            e.Handled = true;
        }
    }

    private void OnRootMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 只有点到空白背景（壁纸/遮罩/滚动区空白）才收起，避免点击搜索框、磁贴时误关
        var isBackground = ReferenceEquals(e.OriginalSource, WallpaperImage)
            || ReferenceEquals(e.OriginalSource, BackgroundShade)
            || ReferenceEquals(e.OriginalSource, RootGrid)
            || ReferenceEquals(e.OriginalSource, MainScroll)
            || ReferenceEquals(e.OriginalSource, MainStack);
        if (!isBackground) return;

        if (PopupOverlay.Visibility == Visibility.Visible)
        {
            _openCategory = null;
            PopupOverlay.Visibility = Visibility.Collapsed;
        }
        else
        {
            CloseLauncher();
        }
        e.Handled = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseLauncher();

    // ---------- 文件夹宫格 ----------

    private void RebuildFolders()
    {
        FoldersPanel.Children.Clear();
        EmptyHint.Visibility = Cfg.Categories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var cat in Cfg.Categories)
            FoldersPanel.Children.Add(BuildFolderTile(cat));

        if (_editMode)
            FoldersPanel.Children.Add(BuildNewFolderTile());
    }

    private Border BuildFolderTile(Category cat)
    {
        var apps = AppIdsOf(cat);

        // 2列×3行预览：图标 32px，格子留足边距保证完整显示
        var preview = new UniformGrid { Columns = 2, Rows = 3, Margin = new Thickness(16, 14, 16, 8) };
        foreach (var app in apps.Take(6))
        {
            var img = new Image { Width = 32, Height = 32, Source = IconOf(app.Path, 48) };
            preview.Children.Add(img);
        }
        for (int i = apps.Count; i < 6; i++) preview.Children.Add(new Border());

        var name = new TextBlock
        {
            Text = cat.Name,
            FontSize = 14,
            Foreground = (Brush)FindResource("TextBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(preview, 0);
        Grid.SetRow(name, 1);
        grid.Children.Add(preview);
        grid.Children.Add(name);

        var tile = new Border
        {
            Width = 164,
            Height = 178,
            Margin = new Thickness(0, 0, 28, 28),
            CornerRadius = new CornerRadius(20),
            Background = (Brush)FindResource("TileBrush"),
            Cursor = Cursors.Hand,
            Tag = cat,
            Child = grid,
            AllowDrop = true,
        };

        tile.MouseEnter += (_, _) => tile.Background = (Brush)FindResource("TileHoverBrush");
        tile.MouseLeave += (_, _) => tile.Background = (Brush)FindResource("TileBrush");
        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            OpenFolderPopup(cat);
        };
        tile.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowFolderContextMenu(cat, tile);
        };
        tile.Drop += (_, e) => OnAppDropped(e, cat.Id);
        tile.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
            tile.Background = (Brush)FindResource("AccentBrush");
        };
        tile.DragLeave += (_, _) => tile.Background = (Brush)FindResource("TileBrush");

        if (_editMode)
        {
            var remove = new Button
            {
                Content = "×",
                Width = 24, Height = 24,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 6, 0),
                Cursor = Cursors.Hand,
                Tag = "remove",
            };
            StyleRemoveButton(remove);
            remove.Click += (_, e) =>
            {
                e.Handled = true;
                DeleteCategory(cat);
            };
            grid.Children.Add(remove);
        }

        return tile;
    }

    /// <summary>右键文件夹磁贴：打开 / 重命名 / 删除（删除有确认）。</summary>
    private void ShowFolderContextMenu(Category cat, Border tile)
    {
        var menu = new ContextMenu();

        var rename = new MenuItem { Header = "✎ 重命名（F2）" };
        rename.Click += (_, _) => RenameCategory(cat);
        menu.Items.Add(rename);

        var delete = new MenuItem { Header = "🗑 删除文件夹" };
        delete.Click += (_, _) => DeleteCategory(cat);
        menu.Items.Add(delete);

        menu.PlacementTarget = tile;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private Border BuildNewFolderTile()
    {
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new TextBlock { Text = "+", FontSize = 34, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(new TextBlock
        {
            Text = "新建文件夹",
            FontSize = 12,
            Foreground = (Brush)FindResource("SubTextBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        });

        var tile = new Border
        {
            Width = 164,
            Height = 178,
            Margin = new Thickness(0, 0, 28, 28),
            CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = content,
        };
        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            CreateCategory();
        };
        return tile;
    }

    // ---------- 文件夹展开面板 ----------

    /// <summary>供外部（调试参数）按类别 Id 打开文件夹面板。</summary>
    public Category? HasCategory(string id) =>
        string.IsNullOrEmpty(id) ? null : Cfg.FindCategory(id);

    public void OpenFolderPopupById(string id)
    {
        var cat = Cfg.FindCategory(id);
        if (cat != null) OpenFolderPopup(cat);
    }

    /// <summary>调试：以编辑模式打开文件夹面板并预选若干应用，展示批量操作条。</summary>
    public void OpenFolderPopupEditDebug(string id, int preselectCount)
    {
        var cat = Cfg.FindCategory(id);
        if (cat == null) return;
        _editMode = true;
        EditButton.Content = "✓ 完成";
        RebuildFolders();
        OpenFolderPopup(cat);
        foreach (var appId in cat.AppIds.Take(preselectCount))
            _popupSelected.Add(appId);
        RebuildPopupApps();
        RefreshBatchBar();
    }

    /// <summary>调试：非编辑模式下面板选择模式 + 预选若干应用。</summary>
    public void OpenFolderPopupSelectDebug(string id, int preselectCount)
    {
        var cat = Cfg.FindCategory(id);
        if (cat == null) return;
        OpenFolderPopup(cat);
        _popupSelectMode = true;
        PopupSelectButton.Content = "✓ 完成";
        foreach (var appId in cat.AppIds.Take(preselectCount))
            _popupSelected.Add(appId);
        RebuildPopupApps();
        RefreshBatchBar();
    }

    private void OpenFolderPopup(Category cat)
    {
        _openCategory = cat;
        ExitSelectMode(); // 切换文件夹时清空批量选择
        PopupTitle.Text = cat.Name;
        // 重命名始终可用（也可按 F2）；添加/删除属于管理操作保留在编辑模式（面板内另有常驻「+」磁贴）
        PopupRenameButton.Visibility = Visibility.Visible;
        PopupAddButton.Visibility = _editMode ? Visibility.Visible : Visibility.Collapsed;
        PopupDeleteButton.Visibility = _editMode ? Visibility.Visible : Visibility.Collapsed;
        RebuildPopupApps();
        PopupOverlay.Visibility = Visibility.Visible;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130));
        PopupOverlay.BeginAnimation(OpacityProperty, fade);
        var scaleUp = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuadraticEase() };
        PopupPanel.RenderTransform = new ScaleTransform(1, 1);
        PopupPanel.RenderTransformOrigin = new Point(0.5, 0.5);
        PopupPanel.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUp);
        PopupPanel.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUp);
    }

    private void RebuildPopupApps()
    {
        if (_openCategory == null) return;
        var cat = _openCategory;
        var apps = AppIdsOf(cat);
        PopupCount.Text = apps.Count == 0 ? "（空）" : $"{apps.Count} 个应用";

        PopupAppsPanel.Children.Clear();
        foreach (var app in apps)
            PopupAppsPanel.Children.Add(BuildAppTile(app, cat));

        // 「+ 添加应用」磁贴常驻（手机桌面习惯：点开文件夹即可加应用）
        var add = BuildAddAppTile();
        add.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            OpenAddAppDialog(cat);
        };
        PopupAppsPanel.Children.Add(add);
        RefreshBatchBar();
    }

    private void OnPopupOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 只在点到遮罩（而非面板）时关闭
        if (ReferenceEquals(e.OriginalSource, PopupOverlay))
        {
            _openCategory = null;
            PopupOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void OnPopupClose(object sender, RoutedEventArgs e)
    {
        _openCategory = null;
        PopupOverlay.Visibility = Visibility.Collapsed;
    }

    // ---------- 应用磁贴 ----------

    private Border BuildAppTile(AppEntry app, Category cat)
    {
        var img = new Image { Width = 60, Height = 60, Source = IconOf(app.Path, 64), HorizontalAlignment = HorizontalAlignment.Center };
        var name = new TextBlock
        {
            Text = app.Name,
            FontSize = 12.5,
            Foreground = (Brush)FindResource("TextBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 40,
            Margin = new Thickness(2, 8, 2, 0),
        };
        var sp = new StackPanel { Margin = new Thickness(0, 10, 0, 6) };
        sp.Children.Add(img);
        sp.Children.Add(name);

        var tile = new Border
        {
            Width = 116,
            Height = 136,
            Margin = new Thickness(0, 0, 14, 14),
            CornerRadius = new CornerRadius(16),
            Background = (Brush)FindResource("TileBrush"),
            Cursor = Cursors.Hand,
            Tag = app,
            AllowDrop = true,
            // Child 延后设置：编辑模式要先把 sp 装进带 × 按钮的 Grid，
            // 若先挂 sp 再改挂 Grid 会报"已是另一个元素的逻辑子元素"
        };
        tile.MouseEnter += (_, _) => tile.Background = TileBgOf(app, true);
        tile.MouseLeave += (_, _) => tile.Background = TileBgOf(app);
        tile.MouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(null);
        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_wasDragging) { _wasDragging = false; return; } // 刚结束拖拽排序，不当作点击
            if (SelectActive) TogglePopupSelection(app, tile); // 选择/编辑模式：点选进入批量操作
            else Launch(app);
        };
        tile.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowAppContextMenu(app, cat, tile, e.GetPosition(this));
        };
        if (SelectActive && _popupSelected.Contains(app.Id))
            tile.Background = SelectedTileBrush();

        // 拖拽排序：落点在目标磁贴左半=插到它前面，右半=后面
        tile.DragOver += (_, e) =>
        {
            if (!e.Data.GetDataPresent(DragFormat)) return;
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            tile.Background = (Brush)FindResource("TileHoverBrush");
        };
        tile.DragLeave += (_, _) => tile.Background = TileBgOf(app);
        tile.Drop += (_, e) =>
        {
            if (!e.Data.GetDataPresent(DragFormat)) return;
            e.Handled = true;
            var dragId = (string)e.Data.GetData(DragFormat);
            if (dragId == app.Id) return;

            var targetIndex = cat.AppIds.IndexOf(app.Id);
            if (targetIndex < 0) return;
            cat.AppIds.Remove(dragId);
            targetIndex = cat.AppIds.IndexOf(app.Id); // 移除后重取目标索引
            if (targetIndex < 0) { cat.AppIds.Add(dragId); }
            else
            {
                var p = e.GetPosition(tile);
                var insert = p.X < tile.ActualWidth / 2 ? targetIndex : targetIndex + 1;
                cat.AppIds.Insert(Math.Clamp(insert, 0, cat.AppIds.Count), dragId);
            }
            App.Instance.ConfigStore.Save();
            RebuildFolders();     // 主宫格预览顺序同步
            RebuildPopupApps();   // 面板内按新顺序重建
        };

        if (SelectActive)
        {
            var remove = new Button
            {
                Content = "×",
                Width = 22,
                Height = 22,
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 4, 0),
                Cursor = Cursors.Hand,
            };
            StyleRemoveButton(remove);
            remove.Click += (_, e) =>
            {
                e.Handled = true;
                cat.AppIds.Remove(app.Id);
                SaveAndRefresh();
                ShowToast($"已从「{cat.Name}」移除 {app.Name}");
            };
            var g = new Grid();
            g.Children.Add(sp);
            g.Children.Add(remove);
            tile.Child = g;
        }
        else
        {
            tile.Child = sp;
        }

        // 拖拽源：任何模式下按住并移动超过系统阈值才进入拖拽（不影响单击）
        tile.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            _wasDragging = true;
            var data = new DataObject(DragFormat, app.Id);
            DragDrop.DoDragDrop(tile, data, DragDropEffects.Move);
        };

        return tile;
    }

    // ---------- 编辑模式批量选择 ----------

    private static Brush SelectedTileBrush()
    {
        var b = new SolidColorBrush(Color.FromArgb(0x66, 0x5B, 0x8C, 0xFF));
        b.Freeze();
        return b;
    }

    private Brush TileBgOf(AppEntry app, bool hover = false)
    {
        if (SelectActive && _popupSelected.Contains(app.Id)) return SelectedTileBrush();
        return (Brush)FindResource(hover ? "TileHoverBrush" : "TileBrush");
    }

    private void TogglePopupSelection(AppEntry app, Border tile)
    {
        if (_popupSelected.Contains(app.Id)) _popupSelected.Remove(app.Id);
        else _popupSelected.Add(app.Id);
        tile.Background = TileBgOf(app);
        RefreshBatchBar();
    }

    private void RefreshBatchBar()
    {
        var visible = SelectActive && _popupSelected.Count > 0;
        BatchBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        BatchCount.Text = $"已选 {_popupSelected.Count} 项";
    }

    private void OnToggleSelectMode(object sender, RoutedEventArgs e)
    {
        _popupSelectMode = !_popupSelectMode;
        PopupSelectButton.Content = _popupSelectMode ? "✓ 完成" : "☐ 选择";
        if (!_popupSelectMode) _popupSelected.Clear();
        RebuildPopupApps();
        RefreshBatchBar();
    }

    private void ExitSelectMode()
    {
        if (!_popupSelectMode) return;
        _popupSelectMode = false;
        PopupSelectButton.Content = "☐ 选择";
        _popupSelected.Clear();
    }

    private void OnBatchClear(object sender, RoutedEventArgs e)
    {
        _popupSelected.Clear();
        if (_openCategory != null) RebuildPopupApps();
        RefreshBatchBar();
    }

    private void OnBatchRemove(object sender, RoutedEventArgs e)
    {
        if (_popupSelected.Count == 0) return;
        var names = string.Join("、", _popupSelected
            .Select(id => Cfg.FindApp(id)?.Name)
            .Where(n => n != null).Take(3));
        var count = _popupSelected.Count;

        foreach (var id in _popupSelected.ToList())
        {
            var entry = Cfg.FindApp(id);
            if (entry == null) continue;
            Cfg.Apps.Remove(entry);
            foreach (var cat in Cfg.Categories)
                cat.AppIds.Remove(entry.Id);
        }
        _popupSelected.Clear();
        SaveAndRefresh();
        if (_openCategory != null) OpenFolderPopup(_openCategory); // 重建面板与批量条
        ShowToast($"已移除 {count} 个应用（{names}{(count > 3 ? " 等" : "")}，原文件未动）");
    }

    private void OnBatchMove(object sender, RoutedEventArgs e)
    {
        if (_popupSelected.Count == 0 || sender is not Button btn) return;
        var menu = new ContextMenu();
        foreach (var cat in Cfg.Categories)
        {
            var target = cat;
            var item = new MenuItem { Header = $"移入「{target.Name}」" };
            item.Click += (_, _) =>
            {
                var ids = _popupSelected.ToList();
                _popupSelected.Clear();
                foreach (var id in ids) MoveAppToCategory(id, target.Id);
                if (_openCategory != null) OpenFolderPopup(_openCategory);
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = btn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private Border BuildAddAppTile()
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = "+", FontSize = 34, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = "添加应用", FontSize = 12, Foreground = (Brush)FindResource("SubTextBrush"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
        return new Border
        {
            Width = 116,
            Height = 136,
            Margin = new Thickness(0, 0, 14, 14),
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = sp,
        };
    }

    private void StyleRemoveButton(Button btn)
    {
        // 圆形半透明深色小按钮
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0xC0, 0x1E, 0x24, 0x32)));
        border.SetValue(Border.PaddingProperty, new Thickness(0));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        btn.Template = template;
        btn.Foreground = Brushes.White;
        btn.FontSize = 10;
    }

    private void OnAppDropped(DragEventArgs e, string targetCategoryId)
    {
        if (!e.Data.GetDataPresent(DragFormat)) return;
        var appId = (string)e.Data.GetData(DragFormat);
        MoveAppToCategory(appId, targetCategoryId);
        e.Handled = true;
    }

    /// <summary>右键磁贴：统一自制菜单（启动/移入/从启动器移除/Windows 菜单），任何模式下删除入口都可见。</summary>
    private void ShowAppContextMenu(AppEntry app, Category current, Border tile, System.Windows.Point relativePos)
    {
        var menu = new ContextMenu();

        var openItem = new MenuItem { Header = $"启动 {app.Name}" };
        openItem.Click += (_, _) => Launch(app);
        menu.Items.Add(openItem);

        foreach (var cat in Cfg.Categories.Where(c => c.Id != current.Id))
        {
            var target = cat;
            var item = new MenuItem { Header = $"移入「{target.Name}」" };
            item.Click += (_, _) => MoveAppToCategory(app.Id, target.Id);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var removeItem = new MenuItem { Header = "从启动器移除（不删文件）" };
        removeItem.Click += (_, _) => RemoveAppFromLauncher(app);
        menu.Items.Add(removeItem);

        var shellItem = new MenuItem { Header = "Windows 菜单（复制 / 打开位置 / 压缩…）" };
        shellItem.Click += (_, _) =>
        {
            var pos = tile.PointToScreen(new System.Windows.Point(0, tile.ActualHeight));
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (!ShellContextMenu.Show(h, app.Path, pos))
                ShowToast("系统菜单不可用，可到资源管理器中操作");
        };
        menu.Items.Add(shellItem);

        menu.PlacementTarget = tile;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>从启动器彻底移除应用（仅删配置，不动任何文件）。</summary>
    private void RemoveAppFromLauncher(AppEntry app)
    {
        var entry = Cfg.Apps.FirstOrDefault(a => a.Id == app.Id);
        if (entry != null)
        {
            Cfg.Apps.Remove(entry);
            foreach (var cat in Cfg.Categories)
                cat.AppIds.Remove(entry.Id);
        }
        SaveAndRefresh();
        ShowToast($"已从启动器移除 {app.Name}（原文件未动，可重新添加）");
    }

    private void MoveAppToCategory(string appId, string targetCategoryId)
    {
        var app = Cfg.FindApp(appId);
        var target = Cfg.FindCategory(targetCategoryId);
        if (app == null || target == null) return;

        var source = Cfg.Categories.FirstOrDefault(c => c.AppIds.Contains(appId));
        if (source?.Id == target.Id) return; // 已在该类别

        if (source != null) source.AppIds.Remove(appId);
        if (!target.AppIds.Contains(appId)) target.AppIds.Add(appId);

        SaveAndRefresh();
        ShowToast($"已把 {app.Name} 移入「{target.Name}」");
    }

    // ---------- 启动应用 ----------

    private void Launch(AppEntry app)
    {
        if (!LaunchService.Launch(app, out var error))
        {
            ShowToast($"启动失败：{error}");
            return;
        }
        if (Cfg.CloseOnLaunch) CloseLauncher();
    }

    // ---------- 编辑：类别 ----------

    private void OnToggleEdit(object sender, RoutedEventArgs e)
    {
        _editMode = !_editMode;
        _popupSelected.Clear(); // 退出/进入编辑模式清空批量选择
        EditButton.Content = _editMode ? "✓ 完成" : "✏ 编辑";
        RebuildFolders();
        if (PopupOverlay.Visibility == Visibility.Visible && _openCategory != null)
            OpenFolderPopup(_openCategory); // 刷新按钮可见性
        RefreshBatchBar();
    }

    private void CreateCategory()
    {
        WithDialogScope(() =>
        {
            var name = InputDialog.Show(this, "新建文件夹", "文件夹名称", "");
            if (string.IsNullOrWhiteSpace(name)) return;
            if (Cfg.FindCategoryByName(name) != null)
            {
                MessageBox.Show(this, "已存在同名文件夹。", "桌面启动器");
                return;
            }
            Cfg.Categories.Add(new Category { Name = name.Trim() });
            SaveAndRefresh();
        });
    }

    private void DeleteCategory(Category cat)
    {
        WithDialogScope(() =>
        {
            var apps = AppIdsOf(cat);
            var msg = apps.Count == 0
                ? $"删除文件夹「{cat.Name}」？"
                : $"删除文件夹「{cat.Name}」？其中的 {apps.Count} 个应用将退出启动器（不会删除任何文件）。";
            if (MessageBox.Show(this, msg, "确认删除", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            Cfg.Categories.Remove(cat);
            if (_openCategory?.Id == cat.Id)
            {
                _openCategory = null;
                PopupOverlay.Visibility = Visibility.Collapsed;
            }
            SaveAndRefresh();
        });
    }

    private void OnPopupRename(object sender, RoutedEventArgs e)
    {
        if (_openCategory != null) RenameCategory(_openCategory);
    }

    /// <summary>重命名文件夹（面板按钮 / F2 / 右键菜单共用）。</summary>
    private void RenameCategory(Category cat)
    {
        WithDialogScope(() =>
        {
            var name = InputDialog.Show(this, "重命名文件夹", "文件夹名称", cat.Name);
            if (string.IsNullOrWhiteSpace(name) || name == cat.Name) return;
            if (Cfg.FindCategoryByName(name.Trim()) != null)
            {
                MessageBox.Show(this, "已存在同名文件夹。", "桌面启动器");
                return;
            }
            cat.Name = name.Trim();
            SaveAndRefresh();
            if (_openCategory?.Id == cat.Id) OpenFolderPopup(cat);
        });
    }

    private void OnPopupDelete(object sender, RoutedEventArgs e)
    {
        if (_openCategory != null) DeleteCategory(_openCategory);
    }

    private void OnPopupAddApp(object sender, RoutedEventArgs e)
    {
        if (_openCategory != null) OpenAddAppDialog(_openCategory);
    }

    private void OpenAddAppDialog(Category cat)
    {
        WithDialogScope(() =>
        {
            var dialog = new AddAppDialog { Owner = null };
            if (dialog.ShowDialog() != true) return;

            foreach (var app in dialog.SelectedApps)
            {
                var existing = Cfg.Apps.FirstOrDefault(a => string.Equals(a.Path, app.Path, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    Cfg.Apps.Add(app);
                    existing = app;
                }
                if (!cat.AppIds.Contains(existing.Id))
                    cat.AppIds.Add(existing.Id);
            }
            SaveAndRefresh();
            OpenFolderPopup(cat);
            PreloadNewIcons();
        });
    }

    private void PreloadNewIcons()
    {
        var paths = Cfg.Apps.Select(a => a.Path).ToList();
        var t = new Thread(() => App.Instance.Icons.Preload(paths)) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    // ---------- 搜索 ----------

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        var text = SearchBox.Text.Trim();
        SearchHint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;

        if (string.IsNullOrEmpty(text))
        {
            FoldersPanel.Visibility = Visibility.Visible;
            SearchResultsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        FoldersPanel.Visibility = Visibility.Collapsed;
        SearchResultsPanel.Visibility = Visibility.Visible;
        SearchResultsPanel.Children.Clear();

        var results = Cfg.Apps
            .Where(a => a.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileNameWithoutExtension(a.Path).Contains(text, StringComparison.OrdinalIgnoreCase))
            .Take(40);
        foreach (var app in results)
            SearchResultsPanel.Children.Add(BuildSearchTile(app));
        if (!SearchResultsPanel.Children.OfType<Border>().Any())
        {
            SearchResultsPanel.Children.Add(new TextBlock
            {
                Text = $"没有找到「{text}」，请在编辑模式里添加应用",
                Foreground = (Brush)FindResource("SubTextBrush"),
                FontSize = 14,
                Margin = new Thickness(8, 24, 8, 0),
            });
        }
    }

    private Border BuildSearchTile(AppEntry app)
    {
        var img = new Image { Width = 60, Height = 60, Source = IconOf(app.Path, 64), HorizontalAlignment = HorizontalAlignment.Center };
        var name = new TextBlock
        {
            Text = app.Name,
            FontSize = 12.5,
            Foreground = (Brush)FindResource("TextBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 40,
            Margin = new Thickness(2, 8, 2, 0),
        };
        var sp = new StackPanel { Margin = new Thickness(0, 10, 0, 6) };
        sp.Children.Add(img);
        sp.Children.Add(name);

        var tile = new Border
        {
            Width = 116,
            Height = 136,
            Margin = new Thickness(0, 0, 14, 14),
            CornerRadius = new CornerRadius(16),
            Background = (Brush)FindResource("TileBrush"),
            Cursor = Cursors.Hand,
            Tag = app,
            Child = sp,
        };
        tile.MouseEnter += (_, _) => tile.Background = (Brush)FindResource("TileHoverBrush");
        tile.MouseLeave += (_, _) => tile.Background = (Brush)FindResource("TileBrush");
        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Launch(app);
        };
        return tile;
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var first = SearchResultsPanel.Children.OfType<Border>().FirstOrDefault(b => b.Tag is AppEntry);
        if (first?.Tag is AppEntry app)
        {
            Launch(app);
            e.Handled = true;
        }
    }

    // ---------- 设置 ----------

    public void ShowSettings()
    {
        if (!IsVisible) OpenOnScreenWithMouse();
        WithDialogScope(() =>
        {
            var dialog = new SettingsDialog { Owner = this };
            dialog.ShowDialog();
            RebuildFolders();
        });
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => ShowSettings();

    // ---------- 公共 ----------

    private List<AppEntry> AppIdsOf(Category cat) =>
        cat.AppIds.Select(Cfg.FindApp).Where(a => a != null).Cast<AppEntry>().ToList();

    private static BitmapSource IconOf(string path, int size) => App.Instance.Icons.GetIcon(path, size);

    private void SaveAndRefresh()
    {
        App.Instance.ConfigStore.Save();
        RebuildFolders();
        RebuildPopupApps();
    }

    private void WithDialogScope(Action action)
    {
        _dialogOpen = true;
        try { action(); }
        finally
        {
            _dialogOpen = false;
            if (IsVisible) Activate();
        }
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _toastTimer.Tick += (_, _) =>
        {
            Toast.Visibility = Visibility.Collapsed;
            _toastTimer!.Stop();
        };
        _toastTimer.Start();
    }
}
