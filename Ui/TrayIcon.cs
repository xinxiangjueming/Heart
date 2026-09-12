using Heart.Services;
using System.Runtime.InteropServices;

namespace Heart.Ui;

/// <summary>
/// 系统托盘图标（Win32 Shell_NotifyIcon）：创建一个隐藏消息窗口接收托盘鼠标回调，
/// 左键单击 → 回到界面，右键单击 → 弹出「回到界面 / 退出软件」菜单。
/// 只在"记录中隐藏到托盘"期间存在，窗口恢复时 Dispose。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint WM_APP_TRAY = 0x8000 + 1; // WM_APP + 1：托盘鼠标回调消息
    private const uint NIM_ADD = 0;
    private const uint NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1;
    private const uint NIF_ICON = 0x2;
    private const uint NIF_TIP = 0x4;
    private const uint WM_LBUTTONUP = 0x202;
    private const uint WM_LBUTTONDBLCLK = 0x203;
    private const uint WM_RBUTTONUP = 0x205;
    private const uint TPM_RETURNCMD = 0x100;
    private const uint TPM_RIGHTBUTTON = 0x2;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x10;
    private const int SM_CXSMICON = 49;
    private const int SM_CYSMICON = 50;
    private const nuint MenuRestore = 1;
    private const nuint MenuExit = 2;
    // SetPreferredAppMode 的模式：强制菜单深色 / 浅色（跟随应用主题，而非系统主题）
    private const int PreferredAppForceDark = 2;
    private const int PreferredAppForceLight = 3;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // 窗口类进程内只注册一次，lpfnWndProc 必须指向永不回收的静态委托：
    // 若注册实例委托，实例随 RestoreFromTray 置空被 GC 回收后，类里残留的
    // 函数指针悬空，下次 CreateWindowExW 在 WM_NCCREATE 回调时触发
    // FailFast（"callback was made on a garbage collected delegate"），
    // 表现为记录中点 X 隐藏托盘时进程直接闪退。消息经 HWND 映射分发到实例。
    private static readonly WndProcDelegate s_wndProc = StaticWndProc;
    private static bool s_classRegistered;
    private static readonly Dictionary<nint, TrayIcon> s_byHwnd = new();

    private readonly Action _onRestore;
    private readonly Action _onExit;
    private readonly IntPtr _hwnd;
    private readonly string _iconPath;
    private bool _added;

    public TrayIcon(string iconPath, Action onRestore, Action onExit)
    {
        _iconPath = iconPath;
        _onRestore = onRestore;
        _onExit = onExit;

        var hInstance = GetModuleHandleW(null);
        if (!s_classRegistered)
        {
            var cls = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                hInstance = hInstance,
                lpszClassName = "HeartTrayWnd",
            };
            RegisterClassExW(ref cls); // 重复注册失败可忽略，CreateWindow 仍可用已有类
            s_classRegistered = true;
        }
        _hwnd = CreateWindowExW(0, "HeartTrayWnd", "Heart", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd != IntPtr.Zero)
            s_byHwnd[_hwnd] = this;
    }

    public void Show(string tip)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_APP_TRAY,
            hIcon = LoadAppIcon(),
            szTip = tip,
        };
        _added = Shell_NotifyIconW(NIM_ADD, ref data);
        if (!_added)
            App.ReportCrash(new InvalidOperationException(
                $"Shell_NotifyIcon(NIM_ADD) failed, error={Marshal.GetLastWin32Error()}"));
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = 1,
            };
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _added = false;
        }
        if (_hwnd != IntPtr.Zero)
        {
            s_byHwnd.Remove(_hwnd);
            DestroyWindow(_hwnd);
        }
    }

    private IntPtr LoadAppIcon()
    {
        if (!File.Exists(_iconPath))
            return IntPtr.Zero;
        return LoadImageW(IntPtr.Zero, _iconPath, IMAGE_ICON,
            GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON), LR_LOADFROMFILE);
    }

    // 静态窗口过程：函数指针进了窗口类就要存活整个进程期；托盘消息按 HWND 找回实例分发
    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_TRAY && s_byHwnd.TryGetValue(hWnd, out var icon))
            icon.OnTrayMessage(lParam);
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void OnTrayMessage(IntPtr lParam)
    {
        if (lParam == (IntPtr)WM_LBUTTONUP || lParam == (IntPtr)WM_LBUTTONDBLCLK)
            _onRestore();
        else if (lParam == (IntPtr)WM_RBUTTONUP)
            ShowContextMenu(_hwnd);
    }

    private void ShowContextMenu(IntPtr hWnd)
    {
        // 经典 Win32 菜单默认永远白底，不随应用主题变化；用 uxtheme 的未公开接口
        // 强制菜单跟随应用当前的深浅色（ForceDark / ForceLight），刷新后下一次弹出生效
        var dark = ThemeManager.Instance.ResolvedTheme == Microsoft.UI.Xaml.ElementTheme.Dark;
        SetPreferredAppMode(dark ? PreferredAppForceDark : PreferredAppForceLight);
        AllowDarkModeForWindow(hWnd, 1);
        FlushMenuThemes();

        var loc = LocalizationService.Instance;
        var menu = CreatePopupMenu();
        AppendMenuW(menu, 0, MenuRestore, loc.T("tray_restore"));
        AppendMenuW(menu, 0, MenuExit, loc.T("tray_exit"));
        GetCursorPos(out var point);
        // 先把前台焦点交给消息窗口：点击菜单外时菜单才能自动收起（TrackPopupMenu 的经典要求）
        SetForegroundWindow(hWnd);
        var command = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
            point.X, point.Y, hWnd, IntPtr.Zero);
        DestroyMenu(menu);
        if (command == 1)
            _onRestore();
        else if (command == 2)
            _onExit();
    }

    // ----- Win32 -----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? module);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW cls);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr instance, string name, uint type,
        int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, nuint idNewItem, string newItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    // uxtheme 未公开接口（ ordinal 固定）：控制经典菜单的深浅色渲染
    [DllImport("uxtheme.dll", EntryPoint = "#133")]
    private static extern int AllowDarkModeForWindow(IntPtr hWnd, int allow);

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();
}
