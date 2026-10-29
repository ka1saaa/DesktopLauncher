using System.Runtime.InteropServices;

namespace DesktopLauncher.Services;

/// <summary>
/// 原生 Windows Shell 右键菜单（与资源管理器一致：复制/粘贴/打开文件位置/压缩(第三方扩展)/属性等）。
/// 通过 IShellItem.BindToHandler(BHID_SFUIObject) 拿 IContextMenu，QueryContextMenu 生成菜单，
/// TrackPopupMenuEx 显示，InvokeCommand 执行。可在菜单头部插入自定义项。
/// </summary>
public static class ShellContextMenu
{
    private const uint IdCmdFirst = 100; // Shell 命令 id 起点；1..99 留给自定义项
    private const uint CmfNormal = 0;
    private const uint TpmReturnCmd = 0x100, TpmNonotify = 0x80, TpmLeftalign = 0, TpmRightbutton = 0x2;
    private const int SwShowNormal = 1;
    private const int WmNull = 0x00;

    private const uint MiimId = 0x1, MiimString = 0x40, MiimFtype = 0x100;
    private const uint MftSeparator = 0x800;

    public readonly record struct CustomItem(string Text, Action Action);

    /// <summary>最近一次失败原因（诊断用）。</summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// 显示原生 Shell 菜单。ownerHwnd 必须是前台可见窗口；screenPoint 为物理像素坐标。
    /// 返回 false 表示 Shell 菜单不可用（调用方应回退自制菜单）。
    /// </summary>
    public static bool Show(IntPtr ownerHwnd, string filePath, System.Windows.Point screenPoint,
        IReadOnlyList<CustomItem>? customItems = null)
    {
        LastError = null;
        try
        {
            IContextMenu pcm;
            try
            {
                pcm = CreateContextMenu(filePath, ownerHwnd);
            }
            catch (Exception ex)
            {
                LastError = $"GetUIObjectOf(IContextMenu) 失败: {ex.Message}";
                return false;
            }

            var hMenu = CreatePopupMenu();
            try
            {
                var actions = new Dictionary<uint, Action>();

                // 头部自定义项 + 分隔线
                if (customItems is { Count: > 0 })
                {
                    uint id = 1;
                    foreach (var item in customItems)
                    {
                        if (item.Text == "-")
                        {
                            InsertSeparator(hMenu, (uint)GetMenuItemCount(hMenu));
                            continue;
                        }
                        var mii = new MENUITEMINFO
                        {
                            cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(),
                            fMask = MiimId | MiimString,
                            wID = id,
                            dwTypeData = item.Text,
                        };
                        InsertMenuItem(hMenu, (uint)GetMenuItemCount(hMenu), true, ref mii);
                        actions[id] = item.Action;
                        id++;
                    }
                    InsertSeparator(hMenu, (uint)GetMenuItemCount(hMenu));
                }

                var hr = pcm.QueryContextMenu(hMenu, (uint)GetMenuItemCount(hMenu), IdCmdFirst, 30000, CmfNormal);
                if (hr < 0)
                {
                    LastError = $"QueryContextMenu 失败: hr=0x{hr:X8}";
                    return false;
                }
                if (GetMenuItemCount(hMenu) == 0)
                {
                    LastError = "QueryContextMenu 成功但菜单项为空";
                    return false;
                }

                // TrackPopupMenu 的经典坑：目标窗口不在前台时菜单立即关闭。
                // 强制前台 + 模拟 Alt 键抬起解除菜单互斥锁。
                SetForegroundWindow(ownerHwnd);
                keybd_event(VkMenu, 0, KeyeventfKeyup, UIntPtr.Zero);
                var cmd = TrackPopupMenuEx(hMenu,
                    TpmReturnCmd | TpmNonotify | TpmLeftalign | TpmRightbutton,
                    (int)screenPoint.X, (int)screenPoint.Y, ownerHwnd, IntPtr.Zero);
                PostMessage(ownerHwnd, WmNull, IntPtr.Zero, IntPtr.Zero);
                if (cmd == 0) return true; // 用户取消

                if (cmd < IdCmdFirst)
                {
                    if (actions.TryGetValue((uint)cmd, out var action)) action();
                    return true;
                }

                var ici = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                    hwnd = ownerHwnd,
                    lpVerb = (IntPtr)(cmd - IdCmdFirst), // MAKEINTRESOURCEA 偏移
                    nShow = SwShowNormal,
                };
                pcm.InvokeCommand(ref ici);
                return true;
            }
            finally
            {
                DestroyMenu(hMenu);
                Marshal.ReleaseComObject(pcm);
            }
        }
        catch (Exception ex)
        {
            LastError = $"异常: {ex.Message}";
            return false; // 任何 COM/菜单失败 → 回退自制菜单
        }
    }

    /// <summary>
    /// 经典路径获取 IContextMenu：SHGetDesktopFolder → 解析父目录 → BindToObject(IShellFolder)
    /// → 解析完整文件 → 取末级 ITEMID → GetUIObjectOf。对隐藏属性文件同样有效。
    /// 注意：GetUIObjectOf 的数组/对象 marshaling 在 .NET COM interop 下会 AV，
    /// 必须用裸指针（AllocHGlobal 构造 apidl、IntPtr 接 ppv 再 GetObjectForIUnknown）。
    /// </summary>
    private static IContextMenu CreateContextMenu(string path, IntPtr hwnd)
    {
        var iidShellFolder = new Guid("000214E6-0000-0000-C000-000000000046"); // IID_IShellFolder
        var iidContextMenu = new Guid("000214E4-0000-0000-C000-000000000046"); // IID_IContextMenu

        SHGetDesktopFolder(out var desktop);

        var dir = System.IO.Path.GetDirectoryName(path) ?? path;
        var hr1 = desktop.ParseDisplayName(hwnd, IntPtr.Zero, dir, out _, out var pidlDir, out _);
        if (hr1 != 0 || pidlDir == IntPtr.Zero)
            throw new Exception($"ParseDisplayName(目录 {dir}) hr=0x{hr1:X8}");
        try
        {
            var hr2 = desktop.BindToObject(pidlDir, IntPtr.Zero, ref iidShellFolder, out var folder);
            if (hr2 != 0) throw new Exception($"BindToObject hr=0x{hr2:X8}");

            var hr3 = desktop.ParseDisplayName(hwnd, IntPtr.Zero, path, out _, out var pidlFull, out _);
            if (hr3 != 0 || pidlFull == IntPtr.Zero)
                throw new Exception($"ParseDisplayName(文件) hr=0x{hr3:X8}");
            try
            {
                var pidlLast = ILFindLastID(pidlFull);
                var pApidl = Marshal.AllocHGlobal(IntPtr.Size);
                try
                {
                    Marshal.WriteIntPtr(pApidl, pidlLast);
                    var hr4 = folder.GetUIObjectOf(hwnd, 1, pApidl, ref iidContextMenu, IntPtr.Zero, out var ppv);
                    if (hr4 != 0 || ppv == IntPtr.Zero)
                        throw new Exception($"GetUIObjectOf hr=0x{hr4:X8}");
                    return (IContextMenu)Marshal.GetObjectForIUnknown(ppv);
                }
                finally
                {
                    Marshal.FreeHGlobal(pApidl);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidlFull);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidlDir);
        }
    }

    /// <summary>诊断：验证 COM 链路到 QueryContextMenu 为止（不弹菜单）。</summary>
    public static string Diagnose(IntPtr ownerHwnd, string filePath)
    {
        try
        {
            var pcm = CreateContextMenu(filePath, ownerHwnd);
            var hMenu = CreatePopupMenu();
            try
            {
                var hr = pcm.QueryContextMenu(hMenu, 0, IdCmdFirst, 30000, CmfNormal);
                var count = GetMenuItemCount(hMenu);
                return $"QueryContextMenu hr=0x{hr:X8}, 菜单项数={count}";
            }
            finally
            {
                DestroyMenu(hMenu);
                Marshal.ReleaseComObject(pcm);
            }
        }
        catch (Exception ex)
        {
            return $"失败: {ex.Message}";
        }
    }

    private static void InsertSeparator(IntPtr hMenu, uint position)
    {
        var sep = new MENUITEMINFO
        {
            cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(),
            fMask = MiimFtype,
            fType = MftSeparator,
        };
        InsertMenuItem(hMenu, position, true, ref sep);
    }

    // ---------- COM ----------

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, out uint pchEaten,
            out IntPtr ppidl, out uint pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IShellFolder ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, IntPtr apidl,
            ref Guid riid, IntPtr pvReserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl,
            [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(IntPtr idCmd, uint uType, IntPtr pwReserved,
            IntPtr pszName, int cchMax);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitleW;
        public int ptInvokeX;
        public int ptInvokeY;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MENUITEMINFO
    {
        public uint cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public IntPtr hSubMenu;
        public IntPtr hbmpChecked;
        public IntPtr hbmpUnchecked;
        public IntPtr dwItemData;
        public string dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    // ---------- P/Invoke ----------

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHGetDesktopFolder(out IShellFolder ppshf);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool InsertMenuItem(IntPtr hMenu, uint uItem, bool fByPosition, ref MENUITEMINFO lpmii);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hmenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const byte VkMenu = 0x12; // Alt
    private const uint KeyeventfKeyup = 0x2;
}
