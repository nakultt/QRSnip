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

    private static ShellHost? s_current;

    private readonly nint _hwnd;
    private readonly uint _taskbarCreatedMessage;
    private readonly string _assets;
    private nint _icon;
    private int _iconSize;
    private bool _iconLight;
    private bool _trayVisible;

    /// <param name="assets">Folder with TrayIcon-dark.ico / TrayIcon-light.ico (AppIcon.ico as a fallback).</param>
    public ShellHost(string assets)
    {
        s_current = this;
        _assets = assets;
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
        LoadIcon();
    }

    /// <summary>The scan shortcut was pressed.</summary>
    public event EventHandler? ScanRequested;

    /// <summary>The tray icon was clicked with the left button.</summary>
    public event EventHandler? TrayClicked;

    /// <summary>The tray icon was right-clicked; the argument is the cursor position in screen pixels.</summary>
    public event EventHandler<Win32.POINT>? MenuRequested;

    /// <summary>"Open QRSnip" was chosen in the classic menu.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>"Exit" was chosen in the classic menu.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Shortcuts the user can pick from, in fallback order. F10 is the default, like Print Screen for Snipping Tool.</summary>
    public static IReadOnlyList<string> HotkeyOptions { get; } = [.. Hotkeys.Select(h => h.Text)];

    /// <summary>The registered shortcut as shown to the user, or null if every candidate was taken.</summary>
    public string? Hotkey { get; private set; }

    public bool TrayVisible => _trayVisible;

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
                Hotkey = text;
                UpdateTip();
                return text;
            }
        }

        Hotkey = null;
        UpdateTip();
        return null;
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

    /// <summary>A classic notification balloon (shown as a toast on Windows 10/11). Needs the tray icon.</summary>
    public bool ShowBalloon(string title, string text)
    {
        if (!_trayVisible)
        {
            return false;
        }

        var data = CreateIconData();
        data.uFlags |= Win32.NIF_INFO;
        data.dwInfoFlags = Win32.NIIF_USER | Win32.NIIF_LARGE_ICON;
        title.AsSpan(0, Math.Min(title.Length, 63)).CopyTo(new Span<char>(data.szInfoTitle, 64));
        text.AsSpan(0, Math.Min(text.Length, 255)).CopyTo(new Span<char>(data.szInfo, 256));
        return Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, data);
    }

    /// <summary>The plain Win32 menu, used if the Windows 11 style menu can't be shown.</summary>
    public void ShowClassicMenu(Win32.POINT pt)
    {
        var menu = Win32.CreatePopupMenu();
        try
        {
            var scan = Hotkey is null ? "Scan QR code" : $"Scan QR code\t{Hotkey}";
            Win32.AppendMenu(menu, Win32.MF_STRING, MenuScan, scan);
            Win32.AppendMenu(menu, Win32.MF_STRING, MenuOpen, "Open QRSnip");
            Win32.AppendMenu(menu, Win32.MF_SEPARATOR, 0, null);
            Win32.AppendMenu(menu, Win32.MF_STRING, MenuExit, "Exit");

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

    public void Dispose()
    {
        ShowTrayIcon(false);
        Win32.UnregisterHotKey(_hwnd, HotkeyId);
        Win32.DestroyWindow(_hwnd);
        if (_icon != 0)
        {
            Win32.DestroyIcon(_icon);
            _icon = 0;
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
                    TrayClicked?.Invoke(this, EventArgs.Empty);
                    break;
                case Win32.WM_RBUTTONUP:
                case Win32.WM_CONTEXTMENU:
                    Win32.GetCursorPos(out var pt);
                    MenuRequested?.Invoke(this, pt);
                    break;
            }

            return true;
        }

        if (msg == _taskbarCreatedMessage && _trayVisible)
        {
            // Explorer restarted (or the taskbar moved to a monitor with another DPI): add the icon again.
            LoadIcon();
            _trayVisible = false;
            ShowTrayIcon(true);
            return true;
        }

        if (msg is Win32.WM_DPICHANGED or Win32.WM_DISPLAYCHANGE or Win32.WM_SETTINGCHANGE)
        {
            RefreshIcon();
        }

        return false;
    }

    /// <summary>Reloads the icon if the taskbar's DPI or light/dark mode changed.</summary>
    public void RefreshIcon()
    {
        if (LoadIcon() && _trayVisible)
        {
            Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, CreateIconData());
        }
    }

    /// <summary>Loads the tray icon at the taskbar's exact size, so it stays crisp at any scaling.</summary>
    private bool LoadIcon()
    {
        var tray = Win32.FindWindow("Shell_TrayWnd", null);
        var dpi = tray != 0 ? Win32.GetDpiForWindow(tray) : 0;
        if (dpi == 0)
        {
            dpi = Win32.GetDpiForSystem();
        }

        var size = Win32.GetSystemMetricsForDpi(Win32.SM_CXSMICON, dpi);
        var light = SystemTheme.TaskbarUsesLightTheme();
        if (_icon != 0 && size == _iconSize && light == _iconLight)
        {
            return false;
        }

        var themed = Path.Combine(_assets, light ? "TrayIcon-light.ico" : "TrayIcon-dark.ico");
        var path = File.Exists(themed) ? themed : Path.Combine(_assets, "AppIcon.ico");
        var icon = Win32.LoadImage(0, path, Win32.IMAGE_ICON, size, size, Win32.LR_LOADFROMFILE);
        if (icon == 0)
        {
            return false;
        }

        if (_icon != 0)
        {
            Win32.DestroyIcon(_icon);
        }

        (_icon, _iconSize, _iconLight) = (icon, size, light);
        return true;
    }

    private void UpdateTip()
    {
        if (_trayVisible)
        {
            Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, CreateIconData());
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
