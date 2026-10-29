using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace DesktopLauncher.Services;

/// <summary>
/// 图标提取：.lnk 的 IconLocation → 目标 exe → 自身文件，逐级回退；
/// 结果转 PNG 缓存到 %APPDATA%\DesktopLauncher\icons\，避免每次重复解析。
/// </summary>
public class IconService
{
    private readonly Dictionary<string, BitmapSource?> _memory = new();
    private readonly HashSet<string> _preloaded = new();

    public BitmapSource GetIcon(string launchPath, int size = 48)
    {
        var key = launchPath.ToLowerInvariant();
        if (_memory.TryGetValue(key, out var cached))
            return cached ?? FallbackIcon(size);

        var bmp = LoadFromDisk(key) ?? Extract(launchPath, size);
        if (bmp != null)
        {
            _memory[key] = bmp;
            SaveToDisk(key, bmp);
            return bmp;
        }
        _memory[key] = null;
        return FallbackIcon(size);
    }

    /// <summary>后台预热：把配置里所有应用的图标提前解析进缓存。</summary>
    public void Preload(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            var key = p.ToLowerInvariant();
            if (_preloaded.Contains(key)) continue;
            _preloaded.Add(key);
            try { GetIcon(p); } catch { /* 单个失败不影响整体 */ }
        }
    }

    private static string DiskPath(string key) => Path.Combine(ConfigStore.IconsDir, HashPath(key) + ".png");

    private static BitmapSource? LoadFromDisk(string key)
    {
        try
        {
            var file = DiskPath(key);
            if (!File.Exists(file)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(file);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static void SaveToDisk(string key, BitmapSource source)
    {
        try
        {
            var file = DiskPath(key);
            if (File.Exists(file)) return;
            using var fs = new FileStream(file, FileMode.Create, FileAccess.Write);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            encoder.Save(fs);
        }
        catch { /* 缓存写失败可忽略 */ }
    }

    private static BitmapSource? Extract(string launchPath, int size)
    {
        var ext = Path.GetExtension(launchPath).ToLowerInvariant();

        // 1) .lnk：优先 IconLocation 指定的图标资源
        if (ext == ".lnk")
        {
            var info = LnkParser.Parse(launchPath);
            if (!string.IsNullOrWhiteSpace(info.IconLocation))
            {
                var parts = info.IconLocation.Split(',');
                var iconFile = Environment.ExpandEnvironmentVariables(parts[0].Trim());
                var iconIndex = parts.Length > 1 && int.TryParse(parts[1].Trim(), out var i) ? i : 0;
                if (File.Exists(iconFile))
                {
                    var bmp = ExtractFromResource(iconFile, iconIndex, size) ?? ExtractAssociated(iconFile, size);
                    if (bmp != null) return bmp;
                }
            }
            // 2) 目标 exe 的图标
            if (File.Exists(info.TargetPath))
            {
                var bmp = ExtractFromResource(info.TargetPath, 0, size) ?? ExtractAssociated(info.TargetPath, size);
                if (bmp != null) return bmp;
            }
            // 3) lnk 自身（多数是默认链接图标）
            return ExtractAssociated(launchPath, size);
        }

        if (ext == ".exe" && File.Exists(launchPath))
            return ExtractFromResource(launchPath, 0, size) ?? ExtractAssociated(launchPath, size);

        if (File.Exists(launchPath))
            return ExtractAssociated(launchPath, size);

        return null;
    }

    /// <summary>PrivateExtractIcons：可取 exe/ico/dll 内任意索引、任意尺寸的图标。</summary>
    private static BitmapSource? ExtractFromResource(string file, int index, int size)
    {
        var hIcons = new IntPtr[1];
        var ids = new int[1];
        var count = PrivateExtractIcons(file, index, size, size, hIcons, ids, 1, 0);
        if (count > 0 && hIcons[0] != IntPtr.Zero)
        {
            try
            {
                return CreateBitmap(Icon.FromHandle(hIcons[0]), size);
            }
            finally
            {
                DestroyIcon(hIcons[0]);
            }
        }
        return null;
    }

    private static BitmapSource? ExtractAssociated(string file, int size)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(file);
            return icon == null ? null : CreateBitmap(icon, size);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource CreateBitmap(Icon icon, int size)
    {
        // CreateBitmapSourceFromHIcon 走 WIC，正确保留 HICON 的 32bpp alpha；
        // Icon.ToBitmap() 对 PrivateExtractIcons 得到的句柄会丢 alpha（全透明）。
        var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        src.Freeze();
        if (Math.Abs(src.Width - size) < 0.5 && Math.Abs(src.Height - size) < 0.5) return src;
        var scaled = new TransformedBitmap(src, new ScaleTransform(size / src.Width, size / src.Height));
        scaled.Freeze();
        return scaled;
    }

    /// <summary>GDI+ Bitmap（32bppArgb）→ WPF BitmapSource，高质量缩放到目标尺寸。</summary>
    private static BitmapSource BitmapToSource(Bitmap bmp, int size)
    {
        BitmapSource src;
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            // BitmapSource.Create 的指针重载不会立即拷贝像素；必须先复制到托管数组，
            // 否则 UnlockBits 之后读到已释放内存，得到全透明位图。
            var buffer = new byte[data.Stride * bmp.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            src = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96,
                PixelFormats.Bgra32, null, buffer, data.Stride);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        src.Freeze();

        if (bmp.Width == size && bmp.Height == size) return src;
        var scaled = new TransformedBitmap(src, new ScaleTransform((double)size / bmp.Width, (double)size / bmp.Height));
        scaled.Freeze();
        return scaled;
    }

    /// <summary>内置兜底图标：灰底圆角方块 + 问号风格（GDI+ 绘制）。</summary>
    public static BitmapSource FallbackIcon(int size = 48)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(System.Drawing.Color.FromArgb(255, 62, 68, 84));
            g.FillEllipse(bg, 1, 1, size - 2, size - 2);
            using var fg = new SolidBrush(System.Drawing.Color.FromArgb(255, 160, 166, 180));
            using var font = new Font("Segoe UI", size / 3.2f, System.Drawing.FontStyle.Bold);
            var sz = g.MeasureString("?", font);
            g.DrawString("?", font, fg, (size - sz.Width) / 2, (size - sz.Height) / 2);
        }
        return BitmapToSource(bmp, size);
    }

    /// <summary>统计位图的不透明像素（调试用）。</summary>
    public static string CountPixels(BitmapSource src, string tag)
    {
        try
        {
            if (src is TransformedBitmap tb) src = tb.Source;
            var converted = src;
            if (src.Format != PixelFormats.Bgra32)
            {
                converted = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            }
            var w = converted.PixelWidth; var h = converted.PixelHeight;
            var pixels = new byte[w * h * 4];
            converted.CopyPixels(pixels, w * 4, 0);
            int opaque = 0, semi = 0;
            for (int i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] > 250) opaque++;
                else if (pixels[i] > 0) semi++;
            }
            return $"{tag}: {w}x{h} fmt={src.Format} opaque={opaque} semi={semi} total={w * h}\n";
        }
        catch (Exception ex)
        {
            return $"{tag}: 统计失败 {ex.Message}\n";
        }
    }

    public static string HashPath(string path)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..16];
    }

    /// <summary>诊断图标提取链路（调试用）。</summary>
    public static string Diagnose(string path)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"path={path} exists={File.Exists(path)}");
        var ext = Path.GetExtension(path).ToLowerInvariant();
        sb.AppendLine($"ext={ext}");
        if (ext == ".lnk")
        {
            var info = LnkParser.Parse(path);
            sb.AppendLine($"lnk.TargetPath={info.TargetPath} exists={File.Exists(info.TargetPath)}");
            sb.AppendLine($"lnk.IconLocation={info.IconLocation}");
        }

        var hIcons = new IntPtr[1];
        var ids = new int[1];
        var target = ext == ".lnk" ? LnkParser.Parse(path).TargetPath : path;
        if (File.Exists(target))
        {
            var count = PrivateExtractIcons(target, 0, 48, 48, hIcons, ids, 1, 0);
            sb.AppendLine($"PrivateExtractIcons({target}) count={count} hIcon={hIcons[0]}");
            if (count > 0 && hIcons[0] != IntPtr.Zero)
            {
                var icon = Icon.FromHandle(hIcons[0]);
                sb.AppendLine($"icon.Size={icon.Size}");
                var src = Imaging.CreateBitmapSourceFromHIcon(hIcons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                var pixels = new byte[(int)(src.PixelWidth * src.PixelHeight) * 4];
                src.CopyPixels(pixels, (int)src.PixelWidth * 4, 0);
                int opaque = 0, semi = 0;
                for (int i = 3; i < pixels.Length; i += 4)
                {
                    if (pixels[i] > 250) opaque++;
                    else if (pixels[i] > 0) semi++;
                }
                sb.AppendLine($"hicon-bmp {src.PixelWidth}x{src.Height} fmt={src.Format} opaquePx={opaque} semiPx={semi} total={pixels.Length / 4}");
                DestroyIcon(hIcons[0]);
            }
        }

        try
        {
            using var assoc = System.Drawing.Icon.ExtractAssociatedIcon(path);
            sb.AppendLine($"assoc={assoc?.Size}");
        }
        catch (Exception ex) { sb.AppendLine($"assoc fail: {ex.Message}"); }

        return sb.ToString();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U4)]
    private static extern uint PrivateExtractIcons(
        string szFileName, int nIconIndex, int cxIcon, int cyIcon,
        IntPtr[] phicon, int[] piconid, uint nIcons, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
