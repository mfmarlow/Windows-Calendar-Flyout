using System.Runtime.InteropServices;
using static CalendarFlyout.NativeMethods;

namespace CalendarFlyout;

public sealed record TrayMenuItem(int Id, string Text, bool Checked = false, bool IsSeparator = false)
{
    public static TrayMenuItem Separator => new(0, "", IsSeparator: true);
}

/// <summary>
/// Notification-area icon built directly on Shell_NotifyIcon. A hidden window on the UI
/// thread receives the callbacks, so every event fires on the WinUI dispatcher thread.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = WM_APP + 1;
    private const uint IconId = 1;

    private readonly WndProc _wndProc; // held in a field so the GC never collects the callback
    private readonly IntPtr _hwnd;
    private readonly uint _taskbarCreatedMessage;
    private readonly Func<string> _iconPath;
    private IntPtr _hIcon;
    private string _tooltip;
    private bool _disposed;

    public event Action? LeftClick;
    public event Action<int>? MenuCommand;
    public Func<IEnumerable<TrayMenuItem>>? BuildMenu { get; set; }

    public TrayIcon(Func<string> iconPath, string tooltip)
    {
        _iconPath = iconPath;
        _tooltip = tooltip;
        _wndProc = WindowProc;

        var hInstance = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = "CalendarFlyout.TrayWindow",
        };
        RegisterClassEx(ref wc);
        // A real (never shown) top-level window, not message-only, so it receives the
        // TaskbarCreated and WM_SETTINGCHANGE broadcasts.
        _hwnd = CreateWindowEx(0, wc.lpszClassName, "CalendarFlyout tray", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _hIcon = LoadIcon();
        AddToTray();
    }

    public void SetTooltip(string text)
    {
        _tooltip = text.Length > 127 ? text[..126] + "…" : text;
        var data = NewData(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void AddToTray()
    {
        var data = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_ADD, ref data);
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }

    private void ReloadIcon()
    {
        var old = _hIcon;
        _hIcon = LoadIcon();
        var data = NewData(NIF_ICON);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    private IntPtr LoadIcon() => LoadImage(IntPtr.Zero, _iconPath(), IMAGE_ICON,
        GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON), LR_LOADFROMFILE);

    private NOTIFYICONDATA NewData(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _hIcon,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            // NOTIFYICON_VERSION_4: LOWORD(lParam) is the event.
            uint evt = (uint)((long)lParam & 0xFFFF);
            if (evt is NIN_SELECT or NIN_KEYSELECT) LeftClick?.Invoke();
            else if (evt == WM_CONTEXTMENU) ShowMenu();
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreatedMessage)
        {
            AddToTray(); // Explorer restarted
            return IntPtr.Zero;
        }
        if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero &&
            Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            ReloadIcon(); // light/dark taskbar switched
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        if (BuildMenu is null) return;

        var menu = CreatePopupMenu();
        foreach (var item in BuildMenu())
        {
            if (item.IsSeparator) AppendMenu(menu, MF_SEPARATOR, 0, null);
            else AppendMenu(menu, MF_STRING | (item.Checked ? MF_CHECKED : 0), (nuint)item.Id, item.Text);
        }

        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd); // required, or the menu won't close when clicking elsewhere
        int command = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, pt.X, pt.Y, _hwnd, IntPtr.Zero);
        PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);

        if (command != 0) MenuCommand?.Invoke(command);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = NewData(0);
        Shell_NotifyIcon(NIM_DELETE, ref data);
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        DestroyWindow(_hwnd);
    }
}
