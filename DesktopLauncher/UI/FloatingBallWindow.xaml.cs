using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DesktopLauncher.UI;

/// <summary>
/// 悬浮球：单击开/关启动器，拖动贴边隐藏（只露一条边），鼠标碰到滑出，移开自动缩回。
/// </summary>
public partial class FloatingBallWindow : Window
{
    private const double BallSize = 56;
    private const double PeekWidth = 14;     // 贴边时露出的宽度
    private const double SnapThreshold = 120; // 距边缘小于该值松手才贴边隐藏
    private const double DockDurationMs = 220;

    public enum Edge { None, Left, Right, Top, Bottom }

    private Point _dragOffset;
    private bool _isDragging;
    private bool _moved;
    private Edge _edge = Edge.None;
    private DispatcherTimer? _hideTimer;

    public FloatingBallWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => RestorePosition();
        Loaded += (_, _) => ApplyCustomAppearance();
    }

    /// <summary>自定义悬浮球外观：%APPDATA%\DesktopLauncher\ball.png（圆形裁剪显示）。</summary>
    private void ApplyCustomAppearance()
    {
        try
        {
            var path = System.IO.Path.Combine(Services.ConfigStore.AppDataDir, "ball.png");
            if (!System.IO.File.Exists(path)) return;
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            Ball.Background = new System.Windows.Media.ImageBrush(bmp)
            {
                Stretch = System.Windows.Media.Stretch.UniformToFill,
            };
            GridDots.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // 图片加载失败保持默认渐变外观
        }
    }

    private void RestorePosition()
    {
        var cfg = App.Instance.ConfigStore.Config;
        double left = cfg.BallX, top = cfg.BallY;

        // 允许贴边隐藏位（略出屏）存在；完全出屏或非法才重置
        var vl = SystemParameters.VirtualScreenLeft;
        var vt = SystemParameters.VirtualScreenTop;
        var vr = vl + SystemParameters.VirtualScreenWidth;
        var vb = vt + SystemParameters.VirtualScreenHeight;
        var valid =
            left >= 0 && top >= 0 &&
            left >= vl - BallSize && top >= vt - BallSize &&
            left + BallSize <= vr + BallSize && top + BallSize <= vb + BallSize &&
            left + PeekWidth > vl && top + PeekWidth > vt &&          // 至少露出一点
            left + BallSize - PeekWidth < vr && top + BallSize - PeekWidth < vb;
        if (!valid)
        {
            var work = SystemParameters.WorkArea;
            left = work.Right - BallSize - 6;
            top = work.Top + work.Height * 0.4;
        }
        Left = left;
        Top = top;
        _edge = DetectEdge(left, top);
    }

    // ---------- 拖动 ----------

    private void Ball_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _moved = false;
        _hideTimer?.Stop();
        _dragOffset = e.GetPosition(this);
        Ball.CaptureMouse();
    }

    private void Ball_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _dragOffset.X) > 3 || Math.Abs(pos.Y - _dragOffset.Y) > 3)
            _moved = true;
        if (!_moved) return;

        var screen = PointToScreen(pos);
        var source = PresentationSource.FromVisual(this);
        double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1;
        Left = screen.X / dpiX - _dragOffset.X;
        Top = screen.Y / dpiY - _dragOffset.Y;
    }

    private void Ball_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        Ball.ReleaseMouseCapture();

        if (!_moved)
        {
            App.Instance.ToggleLauncher();
            return;
        }

        TryDockOrStay();
        var cfg = App.Instance.ConfigStore.Config;
        cfg.BallX = Left;
        cfg.BallY = Top;
        App.Instance.ConfigStore.Save();
    }

    // ---------- 贴边隐藏 / 滑出 ----------

    private static Edge DetectEdge(double left, double top)
    {
        var vl = SystemParameters.VirtualScreenLeft;
        var vt = SystemParameters.VirtualScreenTop;
        var vr = vl + SystemParameters.VirtualScreenWidth;
        var vb = vt + SystemParameters.VirtualScreenHeight;

        // 哪条边穿出了"球大半没入"的位置就算贴哪条边
        if (left < vl - PeekWidth / 2) return Edge.Left;
        if (top < vt - PeekWidth / 2) return Edge.Top;
        if (left + BallSize > vr + PeekWidth / 2) return Edge.Right;
        if (top + BallSize > vb + PeekWidth / 2) return Edge.Bottom;
        return Edge.None;
    }

    /// <summary>松手后：离某条边足够近则贴边隐藏（动画），否则留在原地。</summary>
    private void TryDockOrStay()
    {
        var cx = Left + BallSize / 2;
        var cy = Top + BallSize / 2;
        var vl = SystemParameters.VirtualScreenLeft;
        var vt = SystemParameters.VirtualScreenTop;
        var vr = vl + SystemParameters.VirtualScreenWidth;
        var vb = vt + SystemParameters.VirtualScreenHeight;

        var dLeft = cx - vl;
        var dRight = vr - cx;
        var dTop = cy - vt;
        var dBottom = vb - cy;
        var min = new[] { dLeft, dRight, dTop, dBottom }.Min();

        if (min > SnapThreshold)
        {
            _edge = Edge.None;
            ClampIntoScreen();
            return;
        }

        _edge = min == dLeft ? Edge.Left
              : min == dRight ? Edge.Right
              : min == dTop ? Edge.Top
              : Edge.Bottom;
        Dock();
    }

    /// <summary>缩进边缘，只露 PeekWidth。</summary>
    private void Dock()
    {
        var vl = SystemParameters.VirtualScreenLeft;
        var vt = SystemParameters.VirtualScreenTop;
        var vr = vl + SystemParameters.VirtualScreenWidth;
        var vb = vt + SystemParameters.VirtualScreenHeight;

        double left = Left, top = Top;
        switch (_edge)
        {
            case Edge.Left: left = vl - BallSize + PeekWidth; top = Clamp(top, vt, vb - BallSize); break;
            case Edge.Right: left = vr - PeekWidth; top = Clamp(top, vt, vb - BallSize); break;
            case Edge.Top: top = vt - BallSize + PeekWidth; left = Clamp(left, vl, vr - BallSize); break;
            case Edge.Bottom: top = vb - PeekWidth; left = Clamp(left, vl, vr - BallSize); break;
        }
        AnimateTo(left, top);
        var cfg = App.Instance.ConfigStore.Config;
        cfg.BallX = left;
        cfg.BallY = top;
        App.Instance.ConfigStore.Save();
    }

    /// <summary>贴边隐藏状态：鼠标碰到露出的边 → 滑出。</summary>
    private void PeekOut()
    {
        _hideTimer?.Stop();
        var vl = SystemParameters.VirtualScreenLeft;
        var vt = SystemParameters.VirtualScreenTop;
        var vr = vl + SystemParameters.VirtualScreenWidth;
        var vb = vt + SystemParameters.VirtualScreenHeight;

        double left = Left, top = Top;
        switch (_edge)
        {
            case Edge.Left: left = vl + 4; break;
            case Edge.Right: left = vr - BallSize - 4; break;
            case Edge.Top: top = vt + 4; break;
            case Edge.Bottom: top = vb - BallSize - 4; break;
        }
        AnimateTo(left, top, keepConfig: false);
    }

    private void Ball_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_edge != Edge.None && !_isDragging) PeekOut();
    }

    private void Ball_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_edge == Edge.None || _isDragging) return;
        // 移开后延迟缩回，避免擦边闪烁
        _hideTimer?.Stop();
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer!.Stop();
            if (!_isDragging) Dock();
        };
        _hideTimer.Start();
    }

    private void AnimateTo(double left, double top, bool keepConfig = true)
    {
        var ease = new QuadraticEase();
        var animL = new DoubleAnimation(left, TimeSpan.FromMilliseconds(DockDurationMs)) { EasingFunction = ease };
        var animT = new DoubleAnimation(top, TimeSpan.FromMilliseconds(DockDurationMs)) { EasingFunction = ease };
        animL.Completed += (_, _) =>
        {
            BeginAnimation(LeftProperty, null);
            Left = left;
        };
        animT.Completed += (_, _) =>
        {
            BeginAnimation(TopProperty, null);
            Top = top;
        };
        BeginAnimation(LeftProperty, animL);
        BeginAnimation(TopProperty, animT);
    }

    private void ClampIntoScreen()
    {
        var vl = SystemParameters.VirtualScreenLeft;
        var vt = SystemParameters.VirtualScreenTop;
        var vr = vl + SystemParameters.VirtualScreenWidth;
        var vb = vt + SystemParameters.VirtualScreenHeight;
        Left = Clamp(Left, vl, vr - BallSize);
        Top = Clamp(Top, vt, vb - BallSize);
    }

    /// <summary>调试：直接贴到指定边缘。</summary>
    public void DebugDock(Edge edge)
    {
        _edge = edge;
        Dock();
    }

    private static double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));

    private void OnOpenLauncher(object sender, RoutedEventArgs e) => App.Instance.ToggleLauncher();
    private void OnSettings(object sender, RoutedEventArgs e) => App.Instance.Launcher.ShowSettings();
    private void OnHideBall(object sender, RoutedEventArgs e) => App.Instance.HideBall();
    private void OnExit(object sender, RoutedEventArgs e) => App.Instance.Shutdown();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoActivate = 0x10;

    /// <summary>启动器打开后调用：把悬浮球重新提到最顶层（不抢焦点，启动器保持激活），
    /// 这样启动器全屏时悬浮球依然可见，再点一次即可收起。</summary>
    public void RaiseToTop()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
    }
}
