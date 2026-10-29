using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DesktopLauncher.Services;

/// <summary>
/// 资源管理器"显示隐藏的文件"开关（HKCU\...\Explorer\Advanced\Hidden）。
/// 开着"显示隐藏文件"时，带 Hidden 属性的桌面图标只会淡化显示而非消失，
/// 因此图标隐藏功能会在隐藏期间把该设置切到"不显示"，恢复时还原。
/// </summary>
internal static class ExplorerSettings
{
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ValueName = "Hidden";

    public const int ShowHidden = 1;      // 显示隐藏的文件、文件夹和驱动器
    public const int DontShowHidden = 2;  // 不显示（默认）

    private static readonly IntPtr HwndBroadcast = new(0xFFFF);
    private const int WmSettingChange = 0x1A;
    private const uint SmtoAbortIfHung = 0x0002;

    public static int GetShowHiddenFiles()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AdvancedKey);
            return key?.GetValue(ValueName) is int v ? v : DontShowHidden;
        }
        catch
        {
            return DontShowHidden;
        }
    }

    public static void SetShowHiddenFiles(int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(AdvancedKey);
        key?.SetValue(ValueName, value, RegistryValueKind.DWord);
        NotifyExplorer();
    }

    /// <summary>广播设置变更，让 Explorer（含桌面）立即重读文件夹选项。</summary>
    public static void NotifyExplorer()
    {
        SendMessageTimeout(HwndBroadcast, WmSettingChange, IntPtr.Zero, "Shell",
            SmtoAbortIfHung, 3000, out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam,
        string lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
}
