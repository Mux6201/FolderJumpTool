using System.Runtime.InteropServices;
using System.Text;

namespace FolderJumpTool;

/// <summary>
/// 所有用到的 Win32 API 声明集中放这里，方便维护。
/// </summary>
internal static class NativeMethods
{
    // ---------- WinEvent hook（全局监听窗口事件，无需注入 DLL）----------

    public const uint EVENT_SYSTEM_DIALOGSTART = 0x0010;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;

    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public const int OBJID_WINDOW = 0;

    public delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    // ---------- 窗口 / 控件查找 ----------

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDlgItem(IntPtr hDlg, int nIDDlgItem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    public delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public const uint GW_OWNER = 4;
    public const uint GW_HWNDNEXT = 2;
    public const uint GW_CHILD = 5;

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    /// <summary>返回指定窗口的子窗口链中 Z 序最顶端的那个；hWnd 传 IntPtr.Zero 表示桌面。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int GetDlgCtrlID(IntPtr hWnd);

    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int MA_NOACTIVATE = 3;

    // 调试用：App 启动默认不再弹控制台（见 App.xaml.cs）。
    // 需要看实时日志时，临时在 App.OnStartup 里加一行 NativeMethods.AllocConsole() 即可。
    [DllImport("kernel32.dll")]
    public static extern bool AllocConsole();

    // ---------- 消息发送 ----------

    public const uint WM_SETTEXT = 0x000C;
    public const uint WM_GETTEXT = 0x000D;
    public const uint WM_GETTEXTLENGTH = 0x000E;
    public const uint BM_CLICK = 0x00F5;
    public const uint CB_SHOWDROPDOWN = 0x014F;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, StringBuilder lParam);

    // ---------- 窗口位置 / 大小 ----------

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    // ---------- DWM：视觉帧边界（诊断悬浮窗与对话框的贴齐偏差）----------

    /// <summary>DWM 报告的"扩展帧边界"（EXTENDED_FRAME_BOUNDS）。
    /// Win11 上对话框四周有圆角 + 阴影装饰，GetWindowRect 只给外接矩形，
    /// 这个属性给的是 DWM 合成后的视觉边界，两者之差就是装饰占掉的偏移。</summary>
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    /// <summary>
    /// 取窗口的"视觉可见矩形"：优先 DWM 扩展帧边界（剔除圆角/阴影等装饰），
    /// DWM 不可用时回退到 GetWindowRect。
    /// 悬浮窗贴齐必须用它——GetWindowRect 会把 Win11 对话框四周 ~7px 的
    /// 阴影区也算进去，导致悬浮窗看着凸出/悬空。
    /// </summary>
    public static bool GetVisualRect(IntPtr hwnd, out RECT rect)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect,
                Marshal.SizeOf<RECT>()) == 0)
            return true;

        return GetWindowRect(hwnd, out rect);
    }

    // ---------- 悬浮窗不抢焦点用 ----------

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TOPMOST = 0x00000008;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // ---------- 托盘图标句柄释放 ----------

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    // ---------- Shell 图标（SHGetFileInfo：区分文件类型/文件夹的系统原生图标） ----------

    public const uint SHGFI_ICON = 0x00000100;
    public const uint SHGFI_SMALLICON = 0x00000001;
    public const uint SHGFI_USEFILEATTRIBUTES = 0x00000010;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    // ---------- 全局热键（Ctrl+G = 首选路径直达）----------

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_CONTROL = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---------- 文件夹选择器导航（SendInput 模拟键盘走地址栏）----------

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_RETURN = 0x0D;
    public const ushort VK_L = 0x4C;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL, wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
