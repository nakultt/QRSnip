using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using QRSnip.Core;
using QRSnip.Native;
using QRSnip.Services;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace QRSnip;

/// <summary>
/// Full-desktop overlay showing a frozen screenshot. In <see cref="ScanMode.Area"/> the user
/// drags a box (or clicks for the whole monitor) and any codes inside it are decoded and shown
/// in a card next to the box. In <see cref="ScanMode.FullScreen"/> every monitor is decoded at
/// once and each code is outlined and numbered.
/// </summary>
public sealed partial class CaptureWindow : Window
{
    private const double MinDrag = 6;
    private const double Gap = 12;
    private const double ChipSize = 28;
    private const int RevealAfterFrames = 6;

    private readonly ScreenShot _shot;
    private readonly AppStorage _storage;
    private readonly nint _hwnd;
    private readonly ObservableCollection<HistoryEntry> _results = [];
    private readonly List<FrameworkElement> _chips = [];
    private readonly List<Polygon> _outlines = [];

    private ScanMode _mode;
    private IReadOnlyList<ScanResult> _found = []; // full screen results
    private int _focused = -1;
    private double _scale = 1; // physical pixels per DIP
    private Point _start;
    private Rect _selection;
    private bool _dragging;
    private bool _busy;
    private bool _closed;
    private bool _started;

    internal CaptureWindow(ScreenShot shot, AppStorage storage, ScanMode mode)
    {
        _shot = shot;
        _storage = storage;
        _mode = mode;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        var bitmap = new WriteableBitmap(shot.Image.Width, shot.Image.Height);
        shot.Image.Pixels.CopyTo(bitmap.PixelBuffer);
        bitmap.Invalidate();
        Shot.Source = bitmap;
        Results.ItemsSource = _results;
        UpdateModeButtons();

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

        CoverDesktop();

        Root.SizeChanged += (_, _) => OnLayoutReady();
        Card.SizeChanged += (_, _) => UpdateCursorAreas();
        Hint.SizeChanged += (_, _) => UpdateCursorAreas();
        Activated += OnActivated;
        Closed += (_, _) =>
        {
            _closed = true;
            OverlayCursor.Detach();
            Shot.Source = null;
        };
    }

    public void Show()
    {
        // Render while cloaked and reveal only once the frozen screenshot has been drawn a few
        // times; otherwise the first visible frame is an empty black window. Activating and
        // bringing it to the front also puts it above borderless full-screen apps.
        Win32.SetDwmInt(_hwnd, Win32.DWMWA_CLOAK, 1);
        Activate();
        CoverDesktop(); // again, in case a DPI change on activation resized us
        Win32.SetForegroundWindow(_hwnd);
        OverlayCursor.Attach(_hwnd);

        var frames = 0;
        var revealed = false;
        void Reveal()
        {
            if (revealed)
            {
                return;
            }

            revealed = true;
            CompositionTarget.Rendering -= OnRendering;
            OverlayCursor.HookTree(_hwnd); // XAML creates its input windows lazily
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

        CompositionTarget.Rendering += OnRendering;
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(400); // safety net if rendering stalls while cloaked
            Reveal();
        });
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
        OverlayCursor.HookTree(_hwnd);

        if (!_started)
        {
            _started = true;
            if (_mode == ScanMode.FullScreen)
            {
                _ = ScanAllAsync();
            }
        }
    }

    // ---- Modes ---------------------------------------------------------------------------

    private void OnAreaModeClick(object sender, RoutedEventArgs e) => SetMode(ScanMode.Area);

    private void OnFullModeClick(object sender, RoutedEventArgs e) => SetMode(ScanMode.FullScreen);

    private void SetMode(ScanMode mode)
    {
        var changed = mode != _mode;
        _mode = mode;
        UpdateModeButtons();
        Root.Focus(FocusState.Programmatic);
        if (!changed || _busy)
        {
            return;
        }

        _storage.Settings.LastScanMode = mode;
        _storage.SaveSettings();

        if (mode == ScanMode.FullScreen)
        {
            _ = ScanAllAsync();
        }
        else
        {
            ClearMarks();
            HideCard();
            ShowHint("", "Drag around a QR code", "Click for whole screen · Esc to cancel", ToDip(ScreenCapture.MonitorUnderCursor()));
        }
    }

    private void UpdateModeButtons()
    {
        AreaModeButton.IsChecked = _mode == ScanMode.Area;
        FullModeButton.IsChecked = _mode == ScanMode.FullScreen;
        if (_mode == ScanMode.FullScreen)
        {
            HintText.Text = "Looking for QR codes…";
            HintSecondary.Text = "Esc to cancel";
        }
    }

    // ---- Full screen ---------------------------------------------------------------------

    /// <summary>Decodes every monitor in parallel and outlines what it finds.</summary>
    private async Task ScanAllAsync()
    {
        ClearMarks();
        HideCard();
        Selection.Visibility = Visibility.Collapsed;
        SizeTag.Visibility = Visibility.Collapsed;
        DimTop.Height = DimMiddle.Height = new GridLength(0);
        DimLeft.Width = DimCenter.Width = new GridLength(0);
        var monitor = ToDip(ScreenCapture.MonitorUnderCursor());
        ShowHint("", "Looking for QR codes on every screen…", "Esc to cancel", monitor);

        _busy = true;
        IReadOnlyList<ScanResult> found;
        try
        {
            found = await Task.Run(() => QrScanner.Scan(_shot.Image, _shot.MonitorRegions));
        }
        finally
        {
            _busy = false;
        }

        if (_closed || _mode != ScanMode.FullScreen)
        {
            return;
        }

        if (found.Count == 0)
        {
            ShowHint("", "No QR codes on screen", "Drag to scan an area · Esc to close", monitor);
            await Task.Delay(1600);
            if (!_closed && _mode == ScanMode.FullScreen && _found.Count == 0 && !_dragging && !_busy)
            {
                CloseOnce();
            }

            return;
        }

        _storage.AddToHistory(found);
        _found = found;
        DrawMarks();
        ShowHint(
            "",
            found.Count == 1 ? "1 QR code found" : $"{found.Count} QR codes found",
            found.Count == 1 ? "Enter to open · Esc to close" : "Tab to switch · Enter to open · Esc to close",
            monitor);

        if (_storage.Settings.AutoCopy)
        {
            ResultPresenter.Copy(found[0].Text);
        }

        if (found.Count == 1)
        {
            FocusCode(0);
        }
        else
        {
            ShowAllCodes();
        }
    }

    private void DrawMarks()
    {
        var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var tint = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        for (var i = 0; i < _found.Count; i++)
        {
            var result = _found[i];
            if (result.Corners.Count < 3)
            {
                continue;
            }

            var points = new PointCollection();
            foreach (var corner in result.Corners)
            {
                points.Add(new Point(corner.X / _scale, corner.Y / _scale));
            }

            var outline = new Polygon
            {
                Points = points,
                Stroke = accent,
                StrokeThickness = 3,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = tint,
                IsHitTestVisible = false,
            };
            Marks.Children.Add(outline);
            _outlines.Add(outline);

            var index = i;
            var chip = new Button
            {
                Content = (i + 1).ToString(System.Globalization.CultureInfo.CurrentCulture),
                Width = ChipSize,
                Height = ChipSize,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(ChipSize / 2),
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            };
            AutomationProperties.SetName(chip, $"Code {i + 1}: {result.Content.Title}");
            chip.Click += (_, _) =>
            {
                FocusCode(index);
                Root.Focus(FocusState.Programmatic);
            };

            var host = new CursorGrid { CursorShape = InputSystemCursorShape.Arrow, Width = ChipSize, Height = ChipSize };
            host.Children.Add(chip);
            var bounds = ToDip(result.Bounds);
            Canvas.SetLeft(host, bounds.X - (ChipSize / 2));
            Canvas.SetTop(host, bounds.Y - (ChipSize / 2));
            Marks.Children.Add(host);
            _chips.Add(host);
        }

        UpdateCursorAreas();
    }

    private void ClearMarks()
    {
        Marks.Children.Clear();
        _chips.Clear();
        _outlines.Clear();
        _found = [];
        _focused = -1;
        UpdateCursorAreas();
    }

    /// <summary>The list of every code in one card.</summary>
    private void ShowAllCodes()
    {
        _focused = -1;
        HighlightOutline();
        _results.Clear();
        for (var i = 0; i < _found.Count; i++)
        {
            _results.Add(new HistoryEntry { Text = _found[i].Text, ScannedAt = DateTimeOffset.Now, Number = i + 1 });
        }

        _selection = Union(_found.Where(r => r.Corners.Count > 0).Select(r => ToDip(r.Bounds)));
        CardStatus.Text = _storage.Settings.AutoCopy ? "First code copied to clipboard" : "Click a number to pick a code";
        ShowCard();
    }

    /// <summary>Shows one code's card next to it (Tab and the arrow keys move between codes).</summary>
    private void FocusCode(int index)
    {
        if (_found.Count == 0)
        {
            return;
        }

        _focused = ((index % _found.Count) + _found.Count) % _found.Count;
        HighlightOutline();
        var result = _found[_focused];
        _results.Clear();
        _results.Add(new HistoryEntry { Text = result.Text, ScannedAt = DateTimeOffset.Now, Number = _found.Count > 1 ? _focused + 1 : null });

        _selection = result.Corners.Count > 0 ? ToDip(result.Bounds) : ToDip(ScreenCapture.MonitorUnderCursor());
        CardStatus.Text = _found.Count > 1 ? $"Code {_focused + 1} of {_found.Count} · Tab for the next" : "Drag to scan an area";
        ShowCard();
    }

    private void HighlightOutline()
    {
        for (var i = 0; i < _outlines.Count; i++)
        {
            _outlines[i].StrokeThickness = i == _focused ? 5 : 3;
            _outlines[i].Opacity = _focused < 0 || i == _focused ? 1 : 0.55;
        }
    }

    private static Rect Union(IEnumerable<Rect> rects)
    {
        var union = Rect.Empty;
        foreach (var r in rects)
        {
            union.Union(r);
        }

        return union.IsEmpty ? new Rect(0, 0, 0, 0) : union;
    }

    // ---- Area ----------------------------------------------------------------------------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        if (_busy || !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (_mode == ScanMode.FullScreen)
        {
            // Dragging always scans an area; switch to it.
            ClearMarks();
            SetMode(ScanMode.Area);
        }

        HideCard();
        SetHintVisible(false);
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

    /// <summary>Clicks on the card and the hint bar must not start a selection.</summary>
    private void OnChromePointerPressed(object sender, PointerRoutedEventArgs e) => e.Handled = true;

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_found.Count < 2)
        {
            return;
        }

        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        int? step = e.Key switch
        {
            VirtualKey.Tab => shift ? -1 : 1,
            VirtualKey.Right or VirtualKey.Down => 1,
            VirtualKey.Left or VirtualKey.Up => -1,
            _ => null,
        };

        if (step is { } s)
        {
            FocusCode(_focused < 0 ? (s > 0 ? 0 : _found.Count - 1) : _focused + s);
            e.Handled = true;
        }
    }

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
            ShowHint("\uE7BA", "No QR code found here. Try a slightly larger box.", "Click for whole screen · Esc to cancel", SelectionMonitor());
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
        if (rect.IsEmpty)
        {
            rect = new Rect(_start, _start);
        }

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

    // ---- Hint, card and cursor -------------------------------------------------------------

    private void ShowHint(string glyph, string text, string secondary, Rect monitor)
    {
        HintIcon.Glyph = glyph;
        HintText.Text = text;
        HintSecondary.Text = secondary;
        SetHintVisible(true);
        PlaceHint(monitor);
    }

    private void SetHintVisible(bool visible)
    {
        Hint.Opacity = visible ? 1 : 0;
        Hint.IsHitTestVisible = visible;
        UpdateCursorAreas();
    }

    private void PlaceHint(Rect monitor)
    {
        Hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(Hint, monitor.X + ((monitor.Width - Hint.DesiredSize.Width) / 2));
        Canvas.SetTop(Hint, monitor.Y + 24);
        UpdateCursorAreas();
    }

    private Rect SelectionMonitor() =>
        ToDip(MonitorAt(new Point(_selection.X + (_selection.Width / 2), _selection.Y + (_selection.Height / 2))));

    private void HideCard()
    {
        Card.Visibility = Visibility.Collapsed;
        UpdateCursorAreas();
    }

    private void ShowCard()
    {
        SizeTag.Visibility = Visibility.Collapsed;
        Card.Visibility = Visibility.Visible;
        Card.Measure(new Size(Card.Width, double.PositiveInfinity));
        var width = Card.Width;
        var height = Card.DesiredSize.Height;
        var sel = _selection;
        var monitor = SelectionMonitor();

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
        UpdateCursorAreas();
    }

    /// <summary>Tells <see cref="OverlayCursor"/> where the arrow should show instead of the crosshair.</summary>
    private void UpdateCursorAreas()
    {
        var areas = new List<Win32.RECT>();
        if (Card.Visibility == Visibility.Visible)
        {
            areas.Add(ToScreen(Card));
        }

        if (Hint.IsHitTestVisible)
        {
            areas.Add(ToScreen(Hint));
        }

        areas.AddRange(_chips.Select(ToScreen));
        OverlayCursor.SetArrowAreas([.. areas]);
    }

    /// <summary>Screen bounds (physical pixels) of an element placed on the overlay canvas.</summary>
    private Win32.RECT ToScreen(FrameworkElement element)
    {
        var left = Canvas.GetLeft(element);
        var top = Canvas.GetTop(element);
        var width = double.IsNaN(element.Width) ? element.ActualWidth : element.Width;
        var height = double.IsNaN(element.Height) ? element.ActualHeight : element.Height;
        return new Win32.RECT
        {
            Left = _shot.Left + (int)Math.Floor(left * _scale),
            Top = _shot.Top + (int)Math.Floor(top * _scale),
            Right = _shot.Left + (int)Math.Ceiling((left + width) * _scale),
            Bottom = _shot.Top + (int)Math.Ceiling((top + height) * _scale),
        };
    }

    // ---- Actions -----------------------------------------------------------------------------

    private async void OnResultAction(object? sender, ResultActionEventArgs e)
    {
        switch (e.Action)
        {
            case ResultAction.Open:
                await OpenAndCloseAsync(e.Entry);
                break;
            case ResultAction.Copy:
                CopyAndClose(e.Entry.Text, "Copied to clipboard");
                break;
            case ResultAction.CopySecret when e.Entry.Content.Secret is { } secret:
                CopyAndClose(secret, "Password copied");
                break;
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
            OverlayCursor.Detach(); // while the window still exists, so the class cursors can be restored
            Close();
        }
    }

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

    /// <summary>A rectangle in screenshot pixels, in window DIPs.</summary>
    private Rect ToDip(PixelRect r) => new(r.X / _scale, r.Y / _scale, r.Width / _scale, r.Height / _scale);
}
