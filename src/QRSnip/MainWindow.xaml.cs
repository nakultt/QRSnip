using System.Collections.Specialized;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QRSnip.Native;
using QRSnip.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace QRSnip;

public sealed partial class MainWindow : Window
{
    private const int WidthDip = 460;
    private const int HeightDip = 680;

    private readonly AppStorage _storage = App.Current.Storage;
    private readonly nint _hwnd;
    private bool _loadingSettings;
    private bool _positioned;

    public MainWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.PreferredMinimumWidth = Scale(380);
            presenter.PreferredMinimumHeight = Scale(520);
        }

        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        };

        HistoryList.ItemsSource = _storage.History;
        _storage.History.CollectionChanged += OnHistoryChanged;
        UpdateEmptyState();
        BuildHotkeyHint();

        var version = typeof(App).Assembly.GetName().Version ?? new Version(1, 0);
        VersionText.Text = $"Version {version.ToString(3)} · MIT License";
    }

    public event EventHandler? CloseRequested;

    public void ShowAndFocus()
    {
        if (!_positioned)
        {
            _positioned = true;
            AppWindow.Resize(new SizeInt32(Scale(WidthDip), Scale(HeightDip)));
            CenterOnWorkArea();
        }

        Win32.SetDwmInt(_hwnd, Win32.DWMWA_TRANSITIONS_FORCEDISABLED, 0);
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Activate();
        Win32.SetForegroundWindow(_hwnd);
    }

    /// <summary>Hides without the fade-out animation, so it is gone before the screen is captured.</summary>
    public void HideInstantly()
    {
        Win32.SetDwmInt(_hwnd, Win32.DWMWA_TRANSITIONS_FORCEDISABLED, 1);
        AppWindow.Hide();
    }

    public void Hide() => AppWindow.Hide();

    private int Scale(int dip) => (int)Math.Round(dip * Win32.GetDpiForWindow(_hwnd) / 96.0);

    private void CenterOnWorkArea()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = AppWindow.Size;
        AppWindow.Move(new PointInt32(area.X + ((area.Width - size.Width) / 2), area.Y + ((area.Height - size.Height) / 2)));
    }

    private void BuildHotkeyHint()
    {
        HotkeyHint.Children.Clear();
        if (App.Current.Hotkey is not { } hotkey)
        {
            return;
        }

        HotkeyHint.Children.Add(Caption("or press"));
        foreach (var key in hotkey.Split(" + "))
        {
            HotkeyHint.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["KeyCapStyle"],
                Child = new TextBlock { Text = key, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] },
            });
        }

        HotkeyHint.Children.Add(Caption("anywhere"));

        static TextBlock Caption(string text) => new()
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 2, 0),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
    }

    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

    private void UpdateEmptyState()
    {
        var empty = _storage.History.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnScanClick(object sender, RoutedEventArgs e) => App.Current.StartScan();

    private void OnClearClick(object sender, RoutedEventArgs e) => _storage.ClearHistory();

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry)
        {
            await ResultPresenter.OpenAsync(entry.Content);
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry)
        {
            ResultPresenter.Copy(entry.Text);
            Flash("Copied to clipboard");
        }
    }

    private void OnCopySecretClick(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { Content.Secret: { } secret })
        {
            ResultPresenter.Copy(secret);
            Flash("Password copied");
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry)
        {
            _storage.Remove(entry);
        }
    }

    private async void Flash(string message)
    {
        StatusText.Text = message;
        await Task.Delay(1800);
        if (StatusText.Text == message)
        {
            StatusText.Text = string.Empty;
        }
    }

    private static HistoryEntry? EntryOf(object sender) => (sender as FrameworkElement)?.DataContext as HistoryEntry;

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        _loadingSettings = true;
        HotkeyCombo.ItemsSource ??= ShellHost.HotkeyOptions;
        HotkeyCombo.SelectedItem = App.Current.Hotkey;
        AutoCopyToggle.IsOn = _storage.Settings.AutoCopy;
        BackgroundToggle.IsOn = _storage.Settings.RunInBackground;
        HistoryToggle.IsOn = _storage.Settings.KeepHistory;
        try
        {
            StartupToggle.IsOn = await StartupService.IsEnabledAsync();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            StartupToggle.IsEnabled = false;
        }

        _loadingSettings = false;
        ShowSettings(true);
    }

    private void OnBackRequested(TitleBar sender, object args) => ShowSettings(false);

    private void ShowSettings(bool show)
    {
        HomePage.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        SettingsPage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        AppTitleBar.IsBackButtonVisible = show;
    }

    private void OnSettingToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        _storage.Settings.AutoCopy = AutoCopyToggle.IsOn;
        _storage.Settings.RunInBackground = BackgroundToggle.IsOn;
        _storage.Settings.KeepHistory = HistoryToggle.IsOn;
        _storage.SaveSettings();
        App.Current.ApplyBackgroundSetting();
    }

    private void OnHotkeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || HotkeyCombo.SelectedItem is not string wanted)
        {
            return;
        }

        var actual = App.Current.ChangeHotkey(wanted);
        if (actual != wanted)
        {
            _loadingSettings = true;
            HotkeyCombo.SelectedItem = actual;
            _loadingSettings = false;
            HotkeyDescription.Text = $"{wanted} is already used by another app, so {actual ?? "no shortcut"} is active.";
        }
        else
        {
            HotkeyDescription.Text = "Starts a scan from anywhere, like Print Screen for Snipping Tool.";
        }

        BuildHotkeyHint();
    }

    private async void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        var wanted = StartupToggle.IsOn;
        var actual = await StartupService.SetEnabledAsync(wanted);
        if (actual != wanted)
        {
            _loadingSettings = true;
            StartupToggle.IsOn = actual;
            _loadingSettings = false;
            StartupDescription.Text = "Turned off in Windows Settings › Apps › Startup. Turn it on there first.";
        }
    }
}
