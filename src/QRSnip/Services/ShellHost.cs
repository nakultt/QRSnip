using System.Runtime.InteropServices;
using QRSnip.Native;

namespace QRSnip.Services;

/// <summary>
/// A hidden Win32 window that owns the global hotkey and the notification-area icon.
/// It lives on the UI thread, so its events are raised on the UI thread too.
/// </summary>
internal sealed unsafe class ShellHost : IDisposable
{
    private const string ClassName = "QRSnip.ShellHost";
    private const int HotkeyId = 0x5152;
    private const uint TrayMessage = Win32.WM_APP + 1;
    private const int MenuScan = 1;
    private const int MenuOpen = 2;
    private const int MenuExit = 3;
    private const uint VK_Q = 0x51;
    private const uint VK_F10 = 0x79;
    private const int SM_CXSMICON = 49;

    private static ShellHost? s_current;

    private readonly nint _hwnd;
    private readonly uint _taskbarCreatedMessage;
    private readonly nint _icon;
    private bool _trayVisible;

    public ShellHost(string iconPath)
    {
        s_current = this;
        var instance = Win32.GetModuleHandle(0);

        fixed (char* name = ClassName)
        {
            var wc = new Win32.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Win32.WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = instance,
                lpszClassName = name,
            };
            Win32.RegisterClassEx(wc);
        }

        _hwnd = Win32.CreateWindowEx(Win32.WS_EX_TOOLWINDOW, ClassName, "QRSnip", Win32.WS_POPUP, 0, 0, 0, 0, 0, 0, instance, 0);
        _taskbarCreatedMessage = Win32.RegisterWindowMessage("TaskbarCreated");

        var size = Win32.GetSystemMetrics(SM_CXSMICON);
        _icon = Win32.LoadImage(0, iconPath, Win32.IMAGE_ICON, size, size, Win32.LR_LOADFROMFILE);
    }

    public event EventHandler? ScanRequested;

    public event EventHandler? OpenRequested;

    public event EventHandler? ExitRequested;

    /// <summary>Shortcuts the user can pick from, in fallback order. F10 is the default, like Print Screen for Snipping Tool.</summary>
    public static IReadOnlyList<string> HotkeyOptions { get; } = [.. Hotkeys.Select(h => h.Text)];

    /// <summary>The registered shortcut as shown to the user, or null if every candidate was taken.</summary>
    public string? Hotkey { get; private set; }

    private static (string Text, uint Modifiers, uint Key)[] Hotkeys =>
    [
        ("F10", 0, VK_F10),
        ("Ctrl + F10", Win32.MOD_CONTROL, VK_F10),
        ("Win + Shift + Q", Win32.MOD_WIN | Win32.MOD_SHIFT, VK_Q),
        ("Ctrl + Alt + Shift + Q", Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_SHIFT, VK_Q),
    ];

    /// <summary>Registers the preferred shortcut, or the first free fallback if another app owns it.</summary>
    public string? RegisterHotkey(string? preferred)
    {
        Win32.UnregisterHotKey(_hwnd, HotkeyId);
        var ordered = Hotkeys.OrderBy(h => h.Text == preferred ? 0 : 1);
        foreach (var (text, modifiers, key) in ordered)
        {
            if (Win32.RegisterHotKey(_hwnd, HotkeyId, modifiers | Win32.MOD_NOREPEAT, key))
            {
                return Hotkey = text;
            }
        }

        return Hotkey = null;
    }

    public void ShowTrayIcon(bool visible)
    {
        if (visible == _trayVisible)
        {
            return;
        }

        var data = CreateIconData();
        _trayVisible = Win32.Shell_NotifyIcon(visible ? Win32.NIM_ADD : Win32.NIM_DELETE, data) && visible;
    }

    public void Dispose()
    {
        ShowTrayIcon(false);
        Win32.UnregisterHotKey(_hwnd, HotkeyId);
        Win32.DestroyWindow(_hwnd);
        if (_icon != 0)
        {
            Win32.DestroyIcon(_icon);
        }

        s_current = null;
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (s_current is { } host && host.Handle(msg, wParam, lParam))
            {
                return 0;
            }
        }
        catch (Exception ex)
        {
            // Never let an exception unwind into native code.
            System.Diagnostics.Debug.WriteLine(ex);
        }

        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private bool Handle(uint msg, nint wParam, nint lParam)
    {
        if (msg == Win32.WM_HOTKEY && wParam == HotkeyId)
        {
            ScanRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }

        if (msg == TrayMessage)
        {
            switch ((uint)(lParam & 0xFFFF))
            {
                case Win32.WM_LBUTTONUP:
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case Win32.WM_RBUTTONUP:
                case Win32.WM_CONTEXTMENU:
                    ShowMenu();
                    break;
            }

            return true;
        }

        if (msg == _taskbarCreatedMessage && _trayVisible)
        {
            // Explorer restarted: the icon is gone, add it again.
            _trayVisible = false;
            ShowTrayIcon(true);
            return true;
        }

        return false;
    }

    private void ShowMenu()
    {
        var menu = Win32.CreatePopupMenu();
        try
        {
            var scan = Hotkey is null ? "Scan QR code" : $"Scan QR code\t{Hotkey}";
            Win32.AppendMenu(menu, Win32.MF_STRING, MenuScan, scan);
            Win32.AppendMenu(menu, Win32.MF_STRING, MenuOpen, "Open QRSnip");
            Win32.AppendMenu(menu, Win32.MF_SEPARATOR, 0, null);
            Win32.AppendMenu(menu, Win32.MF_STRING, MenuExit, "Exit");

            Win32.GetCursorPos(out var pt);
            Win32.SetForegroundWindow(_hwnd);
            var command = Win32.TrackPopupMenuEx(menu, Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON | Win32.TPM_BOTTOMALIGN, pt.X, pt.Y, _hwnd, 0);
            Win32.PostMessage(_hwnd, Win32.WM_NULL, 0, 0);

            var handler = command switch
            {
                MenuScan => ScanRequested,
                MenuOpen => OpenRequested,
                MenuExit => ExitRequested,
                _ => null,
            };
            handler?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            Win32.DestroyMenu(menu);
        }
    }

    private Win32.NOTIFYICONDATAW CreateIconData()
    {
        var data = new Win32.NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(Win32.NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = 1,
            uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP,
            uCallbackMessage = TrayMessage,
            hIcon = _icon,
        };

        var tip = Hotkey is null ? "QRSnip" : $"QRSnip ({Hotkey})";
        tip.AsSpan(0, Math.Min(tip.Length, 127)).CopyTo(new Span<char>(data.szTip, 128));
        return data;
    }
}
