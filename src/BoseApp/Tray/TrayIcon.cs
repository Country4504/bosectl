using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BoseApp.Tray;

/// <summary>托盘右键菜单项。</summary>
public sealed record TrayMenuItem(
    string Text,
    Action? OnClick = null,
    bool Checked = false,
    bool Enabled = true,
    bool Radio = false,
    bool IsDefault = false)
{
    public static readonly TrayMenuItem Separator = new("-");
    public bool IsSeparator => Text == "-";
}

/// <summary>
/// 基于 Win32 Shell_NotifyIcon 的托盘图标（WinUI 3 没有内置托盘支持）。
/// 内部创建一个不可见的顶层窗口接收托盘回调；它与 WinUI 窗口在同一 UI 线程，
/// 由 WinUI 的消息循环一并分发消息，因此所有回调都在 UI 线程上执行。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    public const string WindowClassName = "BoseApp.TrayWindow";

    private const uint WM_NULL = 0x0000;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_APP = 0x8000;
    private const uint WM_TRAYCALLBACK = WM_APP + 1;
    public const uint WM_SHOWAPP = WM_APP + 2;

    private readonly WndProc _wndProc; // 必须保持引用，防止委托被 GC
    private readonly IntPtr _hwnd;
    private readonly uint _taskbarCreatedMsg;
    private IntPtr _hIcon;
    private string _tooltip;
    private bool _added;
    private bool _disposed;

    /// <summary>左键单击托盘图标。</summary>
    public event Action? LeftClick;

    /// <summary>另一个实例启动时请求显示主窗口。</summary>
    public event Action? ShowRequested;

    /// <summary>系统深色/浅色主题切换（需要重绘图标）。</summary>
    public event Action? ThemeChanged;

    /// <summary>右键时调用，返回要显示的菜单项。</summary>
    public Func<IReadOnlyList<TrayMenuItem>>? MenuProvider { get; set; }

    public IntPtr Handle => _hwnd;

    public TrayIcon(string tooltip)
    {
        _tooltip = tooltip;
        _wndProc = WindowProc;
        var hInstance = GetModuleHandleW(null);

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = WindowClassName,
        };
        if (RegisterClassExW(ref wc) == 0)
        {
            int err = Marshal.GetLastWin32Error();
            if (err != 1410) throw new Win32Exception(err); // 1410 = 类已注册
        }

        // 用普通（不可见）顶层窗口而不是 message-only 窗口，才能收到 TaskbarCreated 广播
        _hwnd = CreateWindowExW(0, WindowClassName, "BoseApp Tray", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");
        ApplyMenuTheme();
    }

    // ================================================================ 公共 API

    public void SetIcon(IntPtr hIcon)
    {
        var old = _hIcon;
        _hIcon = hIcon;
        AddOrModify();
        if (old != IntPtr.Zero && old != hIcon) DestroyIcon(old);
    }

    public void SetTooltip(string text)
    {
        _tooltip = text;
        if (_added) AddOrModify();
    }

    public void ShowBalloon(string title, string text, bool warning = false)
    {
        if (!_added) return;
        var data = CreateData();
        data.uFlags = NIF_INFO;
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(text, 255);
        data.dwInfoFlags = warning ? NIIF_WARNING : NIIF_INFO;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    /// <summary>系统任务栏是否为浅色主题（决定图标前景色）。</summary>
    public static bool IsTaskbarLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    /// <summary>托盘图标的推荐像素尺寸（随系统 DPI 变化）。</summary>
    public static int RecommendedIconSize()
    {
        try { return GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem()); }
        catch { return 16; }
    }

    /// <summary>通知已运行的实例显示主窗口。返回是否找到了实例。</summary>
    public static bool SignalExistingInstance()
    {
        var hwnd = FindWindowW(WindowClassName, null);
        if (hwnd == IntPtr.Zero) return false;
        PostMessageW(hwnd, WM_SHOWAPP, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    // ================================================================ 内部实现

    private NOTIFYICONDATAW CreateData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private void AddOrModify()
    {
        if (_disposed) return;
        var data = CreateData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_TRAYCALLBACK;
        data.hIcon = _hIcon;
        data.szTip = Truncate(_tooltip, 127);

        if (_added)
        {
            if (Shell_NotifyIconW(NIM_MODIFY, ref data)) return;
        }
        _added = Shell_NotifyIconW(NIM_ADD, ref data);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_TRAYCALLBACK)
            {
                switch ((uint)(lParam.ToInt64() & 0xFFFF))
                {
                    case WM_LBUTTONUP: LeftClick?.Invoke(); break;
                    case WM_RBUTTONUP: ShowContextMenu(); break;
                }
                return IntPtr.Zero;
            }
            if (msg == WM_SHOWAPP)
            {
                ShowRequested?.Invoke();
                return IntPtr.Zero;
            }
            if (msg == _taskbarCreatedMsg)
            {
                // 资源管理器重启后托盘图标会丢失，需要重新添加
                _added = false;
                AddOrModify();
                return IntPtr.Zero;
            }
            if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero &&
                Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
            {
                ApplyMenuTheme();
                ThemeChanged?.Invoke();
            }
        }
        catch
        {
            // 不能让异常穿过原生回调
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var items = MenuProvider?.Invoke();
        if (items == null || items.Count == 0) return;

        var menu = CreatePopupMenu();
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                uint id = (uint)(i + 1);
                if (it.IsSeparator)
                {
                    AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                    continue;
                }
                uint flags = MF_STRING;
                if (!it.Enabled) flags |= MF_GRAYED;
                if (it.Checked && !it.Radio) flags |= MF_CHECKED;
                AppendMenuW(menu, flags, (UIntPtr)id, it.Text);
                if (it.Checked && it.Radio) CheckMenuRadioItem(menu, id, id, id, MF_BYCOMMAND);
                if (it.IsDefault) SetMenuDefaultItem(menu, id, 0);
            }

            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd); // 否则点击菜单外部时菜单不会消失
            uint cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, pt.X, pt.Y, _hwnd, IntPtr.Zero);
            PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (cmd > 0 && cmd <= items.Count) items[(int)cmd - 1].OnClick?.Invoke();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>
    /// 让 Win32 弹出菜单跟随系统深色/浅色主题。使用 uxtheme 的未公开序号导出
    /// （Windows 10 1903+ 可用，被大量托盘程序采用）；失败时保持默认浅色。
    /// </summary>
    private static void ApplyMenuTheme()
    {
        try
        {
            SetPreferredAppMode(IsTaskbarLight() ? 3 /* ForceLight */ : 2 /* ForceDark */);
            FlushMenuThemes();
        }
        catch { }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose()
    {
        if (_disposed) return;
        if (_added)
        {
            var data = CreateData();
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _added = false;
        }
        _disposed = true;
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        _hIcon = IntPtr.Zero;
        DestroyWindow(_hwnd);
    }

    // ================================================================ P/Invoke

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;
    private const uint NIIF_INFO = 0x1, NIIF_WARNING = 0x2;
    private const uint MF_STRING = 0x0, MF_GRAYED = 0x1, MF_CHECKED = 0x8, MF_SEPARATOR = 0x800, MF_BYCOMMAND = 0x0;
    private const uint TPM_RIGHTBUTTON = 0x2, TPM_NONOTIFY = 0x80, TPM_RETURNCMD = 0x100;
    private const int SM_CXSMICON = 49;

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
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

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
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr hInstance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern bool CheckMenuRadioItem(IntPtr menu, uint first, uint last, uint check, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPos);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();
}
