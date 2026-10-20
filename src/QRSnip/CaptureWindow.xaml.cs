using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using QRSnip.Core;
using QRSnip.Native;
using QRSnip.Services;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace QRSnip;

/// <summary>
/// Full-desktop overlay showing a frozen screenshot. The user drags a box (or clicks for the
/// whole monitor) and any codes inside it are decoded and shown in a card next to the box.
/// </summary>
public sealed partial class CaptureWindow : Window
{
    private const double MinDrag = 6;
    private const double Gap = 12;

    private readonly ScreenShot _shot;
    private readonly AppStorage _storage;
    private readonly nint _hwnd;
    private readonly ObservableCollection<HistoryEntry> _results = [];

    private double _scale = 1; // physical pixels per DIP
    private Point _start;
    private Rect _selection;
    private bool _dragging;
    private bool _busy;
    private bool _closed;

    internal CaptureWindow(ScreenShot shot, AppStorage storage)
    {
        _shot = shot;
        _storage = storage;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        var bitmap = new WriteableBitmap(shot.Image.Width, shot.Image.Height);
        shot.Image.Pixels.CopyTo(bitmap.PixelBuffer);
        bitmap.Invalidate();
        Shot.Source = bitmap;
        Results.ItemsSource = _results;

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        Win32.MakeFrameless(_hwnd);

        Win32.SetDwmInt(_hwnd, Win32.DWMWA_TRANSITIONS_FORCEDISABLED, 1);
        Win32.SetDwmInt(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, Win32.DWMWCP_DONOTROUND);
        Win32.SetDwmInt(_hwnd, Win32.DWMWA_BORDER_COLOR, unchecked((int)Win32.DWMWA_COLOR_NONE));
        unsafe
        {
            Win32.SetWindowSubclass(_hwnd, &CursorSubclassProc, CursorSubclassId, 0);
        }

        CoverDesktop();

        Root.SizeChanged += (_, _) => OnLayoutReady();
        Activated += OnActivated;
        Closed += (_, _) =>
        {
            _closed = true;
            Shot.Source = null;
            unsafe
            {
                Win32.RemoveWindowSubclass(_hwnd, &CursorSubclassProc, CursorSubclassId);
            }
        };
    }

    public void Show()
    {
        // Render while cloaked and reveal only once the frozen screenshot has been drawn a few
        // times; otherwise the first visible frame is an empty black window.
        Win32.SetDwmInt(_hwnd, Win32.DWMWA_CLOAK, 1);
        Activate();
        CoverDesktop(); // again, in case a DPI change on activation resized us
        Win32.SetForegroundWindow(_hwnd);

        var frames = 0;
        var revealed = false;
        void Reveal()
        {
            if (revealed)
            {
                return;
            }

            revealed = true;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
            Win32.SetDwmInt(_hwnd, Win32.DWMWA_CLOAK, 0);
            FadeIn(Dim, 150); // the undimmed screenshot matches the live screen, so this is seamless
        }

        void OnRendering(object? sender, object e)
        {
            if (_closed || (Shot.ActualWidth > 0 && ++frames >= RevealAfterFrames))
            {
                Reveal();
            }
        }

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(400); // safety net if rendering stalls while cloaked
            Reveal();
        });
    }

    private const int RevealAfterFrames = 6;
    private const nuint CursorSubclassId = 1;

    /// <summary>
    /// The window class has an arrow cursor, so on every mouse move Windows resets the cursor
    /// to an arrow before WinUI sets the crosshair again, which flickers. Swallowing
    /// WM_SETCURSOR in the client area leaves the cursor entirely to WinUI.
    /// </summary>
    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static nint CursorSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (msg == Win32.WM_SETCURSOR && (lParam & 0xFFFF) == Win32.HTCLIENT)
        {
            return 1;
        }

        return Win32.DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private static void FadeIn(UIElement element, int milliseconds)
    {
        var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, element);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    /// <summary>
    /// Sizes the window so its <em>client area</em> matches the desktop exactly. WinUI keeps a few
    /// pixels of invisible frame even on a borderless window; left alone they show up as a thin
    /// light line at the screen edges, so they are pushed off-screen instead.
    /// </summary>
    private void CoverDesktop()
    {
        var (x, y, w, h) = (_shot.Left, _shot.Top, _shot.Image.Width, _shot.Image.Height);
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));

        Win32.GetWindowRect(_hwnd, out var outer);
        Win32.GetClientRect(_hwnd, out var client);
        var origin = default(Win32.POINT);
        Win32.ClientToScreen(_hwnd, ref origin);

        var left = origin.X - outer.Left;
        var top = origin.Y - outer.Top;
        var right = outer.Right - outer.Left - client.Right - left;
        var bottom = outer.Bottom - outer.Top - client.Bottom - top;
        if (left != 0 || top != 0 || right != 0 || bottom != 0)
        {
            AppWindow.MoveAndResize(new RectInt32(x - left, y - top, w + left + right, h + top + bottom));
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            // Like Snipping Tool: switching away cancels the capture.
            CloseOnce();
        }
        else
        {
            Root.Focus(FocusState.Programmatic);
        }
    }

    private void OnLayoutReady()
    {
        if (Root.ActualWidth <= 0)
        {
            return;
        }

        _scale = _shot.Image.Width / Root.ActualWidth;
        PlaceHint(ToDip(ScreenCapture.MonitorUnderCursor()));
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        if (_busy || !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        Card.Visibility = Visibility.Collapsed;
        Hint.Opacity = 0;
        _start = point.Position;
        _dragging = true;
        Root.CapturePointer(e.Pointer);
        UpdateSelection(new Rect(_start, _start));
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging)
        {
            UpdateSelection(new Rect(_start, e.GetCurrentPoint(Root).Position));
        }
    }

    private async void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Root.ReleasePointerCaptures();

        if (_selection.Width < MinDrag && _selection.Height < MinDrag)
        {
            // A click scans the whole monitor it landed on.
            UpdateSelection(ToDip(MonitorAt(e.GetCurrentPoint(Root).Position)));
        }

        await ScanSelectionAsync();
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e) => CloseOnce();

    private void OnCardPointerPressed(object sender, PointerRoutedEventArgs e) => e.Handled = true;

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        switch (e.Key)
        {
            case VirtualKey.Escape:
                CloseOnce();
                break;
            case VirtualKey.Enter when _results.FirstOrDefault(r => r.CanOpen) is { } link:
                _ = OpenAndCloseAsync(link);
                break;
            case VirtualKey.C when ctrl && _results.Count > 0:
                CopyAndClose(_results[0].Text, "Copied to clipboard");
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private async Task ScanSelectionAsync()
    {
        var region = new PixelRect(
            (int)Math.Floor(_selection.X * _scale),
            (int)Math.Floor(_selection.Y * _scale),
            (int)Math.Ceiling(_selection.Width * _scale),
            (int)Math.Ceiling(_selection.Height * _scale));

        _busy = true;
        IReadOnlyList<ScanResult> found;
        try
        {
            found = await Task.Run(() => QrScanner.Scan(_shot.Image, region));
        }
        finally
        {
            _busy = false;
        }

        if (_closed)
        {
            return;
        }

        if (found.Count == 0)
        {
            ShowHint("", "No QR code found here. Try a slightly larger box.");
            return;
        }

        _storage.AddToHistory(found);
        _results.Clear();
        foreach (var result in found)
        {
            _results.Add(new HistoryEntry { Text = result.Text, ScannedAt = DateTimeOffset.Now });
        }

        CardStatus.Text = found.Count > 1 ? $"{found.Count} codes found" : "Drag again to scan another area";
        if (_storage.Settings.AutoCopy)
        {
            ResultPresenter.Copy(found[0].Text);
            CardStatus.Text = "Copied to clipboard";
        }

        ShowCard();
    }

    private void UpdateSelection(Rect rect)
    {
        rect.Intersect(new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
        _selection = rect;
        DimTop.Height = new GridLength(rect.Y);
        DimMiddle.Height = new GridLength(rect.Height);
        DimLeft.Width = new GridLength(rect.X);
        DimCenter.Width = new GridLength(rect.Width);

        Selection.Visibility = Visibility.Visible;
        Canvas.SetLeft(Selection, rect.X - 1);
        Canvas.SetTop(Selection, rect.Y - 1);
        Selection.Width = rect.Width + 2;
        Selection.Height = rect.Height + 2;

        SizeText.Text = $"{Math.Round(rect.Width * _scale)} × {Math.Round(rect.Height * _scale)}";
        SizeTag.Visibility = rect.Width > MinDrag || rect.Height > MinDrag ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(SizeTag, rect.X);
        Canvas.SetTop(SizeTag, rect.Y >= 26 ? rect.Y - 26 : rect.Y + 6);
    }

    private void ShowHint(string glyph, string text)
    {
        HintIcon.Glyph = glyph;
        HintText.Text = text;
        Hint.Opacity = 1;
        PlaceHint(ToDip(MonitorAt(new Point(_selection.X + (_selection.Width / 2), _selection.Y + (_selection.Height / 2)))));
    }

    private void PlaceHint(Rect monitor)
    {
        Hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(Hint, monitor.X + ((monitor.Width - Hint.DesiredSize.Width) / 2));
        Canvas.SetTop(Hint, monitor.Y + 24);
    }

    private void ShowCard()
    {
        SizeTag.Visibility = Visibility.Collapsed;
        Card.Visibility = Visibility.Visible;
        Card.Measure(new Size(Card.Width, double.PositiveInfinity));
        var width = Card.Width;
        var height = Card.DesiredSize.Height;
        var sel = _selection;
        var monitor = ToDip(MonitorAt(new Point(sel.X + (sel.Width / 2), sel.Y + (sel.Height / 2))));

        double x, y;
        if (sel.Bottom + Gap + height <= monitor.Bottom - Gap)
        {
            (x, y) = (sel.X + ((sel.Width - width) / 2), sel.Bottom + Gap);
        }
        else if (sel.Top - Gap - height >= monitor.Top + Gap)
        {
            (x, y) = (sel.X + ((sel.Width - width) / 2), sel.Top - Gap - height);
        }
        else if (sel.Right + Gap + width <= monitor.Right - Gap)
        {
            (x, y) = (sel.Right + Gap, sel.Y + ((sel.Height - height) / 2));
        }
        else if (sel.Left - Gap - width >= monitor.Left + Gap)
        {
            (x, y) = (sel.Left - Gap - width, sel.Y + ((sel.Height - height) / 2));
        }
        else
        {
            // The box fills the screen: float the card near the bottom of it.
            (x, y) = (sel.X + ((sel.Width - width) / 2), monitor.Bottom - height - (Gap * 4));
        }

        Canvas.SetLeft(Card, Math.Clamp(x, monitor.Left + Gap, Math.Max(monitor.Left + Gap, monitor.Right - width - Gap)));
        Canvas.SetTop(Card, Math.Clamp(y, monitor.Top + Gap, Math.Max(monitor.Top + Gap, monitor.Bottom - height - Gap)));
    }

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry)
        {
            await OpenAndCloseAsync(entry);
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry)
        {
            CopyAndClose(entry.Text, "Copied to clipboard");
        }
    }

    private void OnCopySecretClick(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { Content.Secret: { } secret })
        {
            CopyAndClose(secret, "Password copied");
        }
    }

    private void OnDoneClick(object sender, RoutedEventArgs e) => CloseOnce();

    private async Task OpenAndCloseAsync(HistoryEntry entry)
    {
        // Close first so the browser is not hidden behind this always-on-top overlay.
        CloseOnce();
        await ResultPresenter.OpenAsync(entry.Content);
    }

    private async void CopyAndClose(string text, string message)
    {
        ResultPresenter.Copy(text);
        CardStatus.Text = message;
        await Task.Delay(650);
        CloseOnce();
    }

    private void CloseOnce()
    {
        if (!_closed)
        {
            _closed = true;
            Close();
        }
    }

    private static HistoryEntry? EntryOf(object sender) => (sender as FrameworkElement)?.DataContext as HistoryEntry;

    /// <summary>Bounds of the monitor containing a point given in window DIPs, in virtual-screen pixels.</summary>
    private Win32.RECT MonitorAt(Point dip)
    {
        var pt = new Win32.POINT
        {
            X = _shot.Left + (int)(dip.X * _scale),
            Y = _shot.Top + (int)(dip.Y * _scale),
        };
        var info = new Win32.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfo(Win32.MonitorFromPoint(pt, Win32.MONITOR_DEFAULTTONEAREST), ref info);
        return info.rcMonitor;
    }

    private Rect ToDip(Win32.RECT r) => new(
        (r.Left - _shot.Left) / _scale,
        (r.Top - _shot.Top) / _scale,
        (r.Right - r.Left) / _scale,
        (r.Bottom - r.Top) / _scale);
}
