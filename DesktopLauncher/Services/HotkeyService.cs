using System.Runtime.InteropServices;
using System.Windows.Interop;
using DesktopLauncher.Models;

namespace DesktopLauncher.Services;

/// <summary>
/// 全局热键：通过隐藏消息窗口注册 RegisterHotKey。
/// 主热键打开/收起启动器；另支持任意多个"文件夹热键"（按下即在资源管理器打开指定目录）。
/// </summary>
public class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    private const int MainId = 0x1000;
    private const int FolderIdBase = 0x2000;

    private HwndSource? _source;
    private bool _mainRegistered;
    private readonly Dictionary<int, string> _folderMap = new(); // id → 文件夹路径

    public event Action? HotkeyPressed;
    public event Action<string>? FolderPressed;

    /// <summary>在 UI 线程调用：创建承载热键消息的隐藏窗口并注册主热键。</summary>
    public bool Start(string modifiers, string key)
    {
        Stop();
        var parameters = new HwndSourceParameters("DesktopLauncherHotkey")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        var mods = ParseModifiers(modifiers) | ModNoRepeat;
        var vk = KeyToVk(key);
        _mainRegistered = RegisterHotKey(_source.Handle, MainId, mods, vk);
        return _mainRegistered;
    }

    /// <summary>注册全部文件夹热键；返回因冲突等原因注册失败的路径列表。</summary>
    public List<string> RegisterFolders(IReadOnlyList<FolderHotkey> hotkeys)
    {
        UnregisterFolders();
        var failed = new List<string>();
        if (_source == null || hotkeys == null) return failed;

        var usedIds = new HashSet<int>();
        foreach (var hk in hotkeys)
        {
            if (string.IsNullOrWhiteSpace(hk.Path) || string.IsNullOrWhiteSpace(hk.Key)) continue;
            var id = FolderIdBase + usedIds.Count;
            var mods = ParseModifiers(hk.Modifiers) | ModNoRepeat;
            if (RegisterHotKey(_source.Handle, id, mods, KeyToVk(hk.Key)))
            {
                _folderMap[id] = hk.Path;
                usedIds.Add(id);
            }
            else
            {
                failed.Add($"{hk.Modifiers}+{hk.Key} → {hk.Path}");
            }
        }
        return failed;
    }

    private void UnregisterFolders()
    {
        if (_source == null) return;
        foreach (var id in _folderMap.Keys)
            UnregisterHotKey(_source.Handle, id);
        _folderMap.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;
        var id = wParam.ToInt32();
        if (id == MainId)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        else if (_folderMap.TryGetValue(id, out var path))
        {
            FolderPressed?.Invoke(path);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Stop()
    {
        if (_source != null)
        {
            if (_mainRegistered) UnregisterHotKey(_source.Handle, MainId);
            UnregisterFolders();
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
            _mainRegistered = false;
        }
    }

    public void Dispose() => Stop();

    public static uint ParseModifiers(string modifiers)
    {
        uint mods = 0;
        foreach (var part in modifiers.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.Trim().ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModControl; break;
                case "alt": mods |= ModAlt; break;
                case "shift": mods |= ModShift; break;
                case "win": mods |= ModWin; break;
            }
        }
        return mods == 0 ? ModControl | ModAlt : mods;
    }

    public static uint KeyToVk(string key)
    {
        var k = key.Trim().ToUpperInvariant();
        if (k.Length == 1 && k[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')
            return k[0];
        return k switch
        {
            "SPACE" => 0x20,
            "TAB" => 0x09,
            _ when k.StartsWith('F') && int.TryParse(k[1..], out var n) && n is >= 1 and <= 12 => (uint)(0x70 + n - 1),
            _ => 0x44, // D
        };
    }

    /// <summary>热键是否可用（例如用户改成非法组合前先校验）。</summary>
    public static bool IsValidKey(string key) => KeyToVk(key) != 0;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
