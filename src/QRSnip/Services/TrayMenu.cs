using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using QRSnip.Native;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace QRSnip.Services;

/// <summary>
/// The notification-area menu in the Windows 11 style: a WinUI <see cref="MenuFlyout"/> (rounded,
/// Fluent icons, light or dark like the taskbar) opened from a tiny, invisible host window at
/// the cursor. The flyout uses a windowed popup, so it can extend beyond its host.
/// </summary>
internal sealed partial class TrayMenu : Window
{
    private readonly Grid _root = new() { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly MenuFlyout _menu = new() { ShouldConstrainToRootBounds = false };
    private readonly nint _hwnd;
    private PointInt32 _at;

    public TrayMenu()
    {
        Content = _root;
        _hwnd = WindowNative.GetWindowHandle(this);

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
        Win32.SetDwmInt(_hwnd, Win32.DWMWA_BORDER_COLOR, unchecked((int)Win32.DWMWA_COLOR_NONE));

        _menu.Closed += (_, _) => AppWindow.Hide();
        Activated += (_, e) =>
        {
            // Clicking anywhere else dismisses the menu, like a native context menu. Check once
            // activation has settled, and ignore our own windows (the menu's popup or a submenu).
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (_menu.IsOpen && !Win32.IsOwnWindow(Win32.GetForegroundWindow()))
                    {
                        _menu.Hide();
                    }
                });
            }
        };
        _root.Loaded += (_, _) => OpenMenu();
    }

    public bool IsOpen => _menu.IsOpen;

    /// <summary>Shows <paramref name="items"/> at a point in screen pixels.</summary>
    public void Show(Win32.POINT at, IEnumerable<MenuFlyoutItemBase> items)
    {
        _menu.Items.Clear();
        foreach (var item in items)
        {
            _menu.Items.Add(item);
        }

        var theme = SystemTheme.TaskbarTheme;
        _root.RequestedTheme = theme;
        _menu.MenuFlyoutPresenterStyle = new Style(typeof(MenuFlyoutPresenter))
        {
            Setters = { new Setter(FrameworkElement.RequestedThemeProperty, theme) },
        };

        // A 1x1 window at the cursor: the menu covers it.
        _at = new PointInt32(at.X, at.Y);
        AppWindow.MoveAndResize(new RectInt32(at.X, at.Y, 1, 1));
        AppWindow.Show(true);
        Activate();
        Win32.SetForegroundWindow(_hwnd);

        if (_root.XamlRoot is not null && _root.IsLoaded)
        {
            OpenMenu();
        }
    }

    private void OpenMenu()
    {
        if (!AppWindow.IsVisible || _menu.IsOpen)
        {
            return;
        }

        // Open away from the screen edge the tray sits on, like the shell's own menus.
        var area = DisplayArea.GetFromPoint(_at, DisplayAreaFallback.Nearest).WorkArea;
        var below = _at.Y < area.Y + (area.Height / 2);
        var right = _at.X < area.X + (area.Width / 2);
        var placement = (below, right) switch
        {
            (true, true) => FlyoutPlacementMode.BottomEdgeAlignedLeft,
            (true, false) => FlyoutPlacementMode.BottomEdgeAlignedRight,
            (false, true) => FlyoutPlacementMode.TopEdgeAlignedLeft,
            (false, false) => FlyoutPlacementMode.TopEdgeAlignedRight,
        };

        _menu.ShowAt(_root, new FlyoutShowOptions
        {
            Position = new Point(0, 0),
            Placement = placement,
            ShowMode = FlyoutShowMode.Standard,
        });
    }
}
