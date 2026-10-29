using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace DesktopLauncher.Services;

/// <summary>读取当前壁纸并预渲染"模糊版"（缩小再放大的低成本模糊，避免实时 BlurEffect 卡顿）。</summary>
public static class WallpaperService
{
    /// <summary>返回适合铺满 targetSize 的壁纸图（已模糊+暗化可由 UI 层叠加遮罩）。</summary>
    public static ImageSource? GetBlurredWallpaper(Size targetSize, int blurDivisor = 24)
    {
        var wall = LoadRawWallpaper();
        if (wall == null) return null;

        // 缩到极小再放大 = 廉价高斯感
        var tiny = new RenderTargetBitmap(
            (int)Math.Max(2, targetSize.Width / blurDivisor),
            (int)Math.Max(2, targetSize.Height / blurDivisor), 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(wall, new Rect(0, 0, tiny.Width, tiny.Height));
        }
        tiny.Render(dv);

        var upscale = new TransformedBitmap(tiny, new ScaleTransform(targetSize.Width / tiny.Width, targetSize.Height / tiny.Height));
        upscale.Freeze();
        return upscale;
    }

    private static BitmapSource? LoadRawWallpaper()
    {
        // 1) TranscodedWallpaper：Explorer 当前实际使用的壁纸副本（对 OneDrive/幻灯片也有效）
        var transcoded = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Themes\TranscodedWallpaper");
        var src = TryLoad(transcoded);
        if (src != null) return src;

        // 2) 注册表里的壁纸路径
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            if (key?.GetValue("WallPaper") is string p)
            {
                var path = Environment.ExpandEnvironmentVariables(p);
                if (!string.IsNullOrEmpty(path))
                    src = TryLoad(path);
                if (src != null) return src;
            }
        }
        catch { /* 走兜底 */ }

        // 3) 主题 CachedFiles
        var cached = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Themes\CachedFiles");
        try
        {
            var img = Directory.EnumerateFiles(cached, "Image_*.*").FirstOrDefault();
            if (img != null) return TryLoad(img);
        }
        catch { /* 无壁纸则由 UI 回退深色渐变 */ }

        return null;
    }

    private static BitmapSource? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}
