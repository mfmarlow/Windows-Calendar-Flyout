using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CalendarFlyout;

internal static class NativeMethods
{
    // ---- window / message plumbing ----
    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public WndProc lpfnWndProc;
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
    public struct POINT { public int X; public int Y; }

    public const uint WM_NULL = 0x0000;
    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_APP = 0x8000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RegisterClassExW")]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateWindowExW")]
    public static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    public static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    public static extern IntPtr GetModuleHandle(string? moduleName);

    // ---- memory ----
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    /// <summary>
    /// Frees unused managed memory and hands idle pages back to Windows, as Windows does for
    /// minimized apps. Pages come back on demand the next time the flyout opens.
    /// </summary>
    public static void TrimMemory()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }

    // ---- menus ----
    public const uint MF_STRING = 0x0000, MF_CHECKED = 0x0008, MF_SEPARATOR = 0x0800;
    public const uint TPM_RIGHTBUTTON = 0x0002, TPM_NONOTIFY = 0x0080, TPM_RETURNCMD = 0x0100;

    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    public static extern bool AppendMenu(IntPtr hMenu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")]
    public static extern int TrackPopupMenuEx(IntPtr hMenu, uint flags, int x, int y, IntPtr hWnd, IntPtr tpmParams);

    // ---- icons ----
    public const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10;
    public const int SM_CXSMICON = 49, SM_CYSMICON = 50;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW")]
    public static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);

    // ---- notification area ----
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_SHOWTIP = 0x80;
    public const uint NOTIFYICON_VERSION_4 = 4;
    public const uint NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion; // union with uTimeout
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    public static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    // ---- monitors / DPI ----
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    public static double ScaleForPoint(POINT pt)
    {
        var monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        return GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
    }

    // ---- theme ----
    public static bool TaskbarUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    // Undocumented but widely used (Explorer, Notepad++, PowerToys): lets classic Win32
    // popup menus follow the system dark/light mode. Safe no-op if unavailable.
    [DllImport("uxtheme.dll", EntryPoint = "#135")] private static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] private static extern void FlushMenuThemes();

    public static void EnableDarkContextMenus()
    {
        try { SetPreferredAppMode(1 /* AllowDark */); FlushMenuThemes(); } catch { }
    }
}
