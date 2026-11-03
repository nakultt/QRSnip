using System.Collections.Specialized;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using QRSnip.Core;
using QRSnip.Native;
using QRSnip.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace QRSnip;

public sealed partial class MainWindow : Window
{
    private const int WidthDip = 460;
    private const int HeightDip = 680;
    private const double WideLayoutWidth = 820;

    private readonly AppStorage _storage = App.Current.Storage;
    private readonly nint _hwnd;
    private bool _loadingSettings;
    private bool _positioned;
    private bool _allowClose;
    private Page _page;
    private long _lastDragOver;

    public MainWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = true;
            presenter.PreferredMinimumWidth = Scale(380);
            presenter.PreferredMinimumHeight = Scale(520);
        }

        AppWindow.Closing += (_, e) =>
        {
            RememberSize();
            if (_allowClose)
            {
                return;
            }

            e.Cancel = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        };

        Root.SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width >= WideLayoutWidth);
        HistoryList.ItemsSource = _storage.History;
        _storage.History.CollectionChanged += OnHistoryChanged;
        App.Current.UpdateStateChanged += OnUpdateStateChanged;
        // The camera only runs while it can be seen: not minimized, hidden or in the tray.
        AppWindow.Changed += (_, _) => UpdateCamera();

        Closed += (_, _) =>
        {
            _ = CameraPage.StopAsync();

            // The window can be rebuilt later; don't let the history or the app keep this one alive.
            _storage.History.CollectionChanged -= OnHistoryChanged;
            App.Current.UpdateStateChanged -= OnUpdateStateChanged;
            HistoryList.ItemsSource = null;
        };
        UpdateEmptyState();
        BuildHotkeyHint();

        var version = typeof(App).Assembly.GetName().Version ?? new Version(1, 0);
        var channel = App.Current.Updates?.ChannelName;
        VersionText.Text = channel is null
            ? $"Version {version.ToString(3)} · MIT License"
            : $"Version {version.ToString(3)} · {channel} · MIT License";
        ShowUpdate();
    }

    public event EventHandler? CloseRequested;

    private enum Page
    {
        Home,
        Settings,
        Pictures,
        Camera,
    }

    public void ShowAndFocus()
    {
        if (!_positioned)
        {
            _positioned = true;
            RestoreSize();
        }

        Win32.SetDwmInt(_hwnd, Win32.DWMWA_TRANSITIONS_FORCEDISABLED, 0);
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Activate();
        Win32.SetForegroundWindow(_hwnd);
        UpdateCamera();
    }

    /// <summary>Hides without the fade-out animation, so it is gone before the screen is captured.</summary>
    public void HideInstantly()
    {
        Win32.SetDwmInt(_hwnd, Win32.DWMWA_TRANSITIONS_FORCEDISABLED, 1);
        AppWindow.Hide();
        UpdateCamera();
    }

    /// <summary>Hides to the notification area; the next time it opens on the home page.</summary>
    public void Hide()
    {
        RememberSize();
        ShowPage(Page.Home);
        AppWindow.Hide();
    }

    /// <summary>Opens the camera page (tray menu, --camera).</summary>
    public void OpenCamera() => ShowPage(Page.Camera);

    /// <summary>Really closes the window, releasing its visual tree.</summary>
    public void Release()
    {
        _allowClose = true;
        Close();
    }

    private int Scale(int dip) => (int)Math.Round(dip * Win32.GetDpiForWindow(_hwnd) / 96.0);

    private bool? _wide;

    /// <summary>
    /// Narrow (the default size): one column. Wide or maximized: the scan panel on the left and
    /// recent scans on the right, so a big window doesn't just stretch one column.
    /// </summary>
    private void ApplyLayout(bool wide)
    {
        if (_wide == wide)
        {
            return;
        }

        _wide = wide;
        MainColumn.Width = wide ? new GridLength(420) : new GridLength(1, GridUnitType.Star);
        SideColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        HomePage.ColumnSpacing = wide ? 28 : 0;
        HomePage.Padding = wide ? new Thickness(28, 8, 28, 16) : new Thickness(20, 4, 20, 12);

        // Recent header, list and empty state move to the right-hand column when wide.
        Place(RecentHeader, column: wide ? 1 : 0, row: wide ? 0 : 1, rowSpan: 1);
        RecentHeader.VerticalAlignment = wide ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        RecentHeader.Margin = wide ? new Thickness(4, 0, 0, 0) : new Thickness(4, 8, 0, 0);
        Place(HistoryList, column: wide ? 1 : 0, row: wide ? 0 : 2, rowSpan: wide ? 4 : 1);
        HistoryList.Margin = wide ? new Thickness(-4, 40, -4, 0) : new Thickness(-4, 0, -4, 0);
        Place(EmptyState, column: wide ? 1 : 0, row: wide ? 0 : 2, rowSpan: wide ? 4 : 1);
        EmptyState.Margin = wide ? new Thickness(0, 120, 0, 0) : new Thickness(0, 24, 0, 0);

        static void Place(FrameworkElement element, int column, int row, int rowSpan)
        {
            Grid.SetColumn(element, column);
            Grid.SetRow(element, row);
            Grid.SetRowSpan(element, rowSpan);
        }
    }

    /// <summary>
    /// Opens compact by default (a quick utility, like Snipping Tool); if the user resized or
    /// maximized it before, that size is restored, clamped to the current screen.
    /// </summary>
    private void RestoreSize()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min(Scale(_storage.Settings.WindowWidth ?? WidthDip), area.Width);
        var height = Math.Min(Scale(_storage.Settings.WindowHeight ?? HeightDip), area.Height);
        AppWindow.Resize(new SizeInt32(width, height));
        CenterOnWorkArea();

        if (_storage.Settings.WindowMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    /// <summary>Saves the window size (in DIPs) and maximized state for the next time it opens.</summary>
    private void RememberSize()
    {
        if (!_positioned || AppWindow.Presenter is not OverlappedPresenter presenter
            || presenter.State == OverlappedPresenterState.Minimized)
        {
            return;
        }

        var settings = _storage.Settings;
        settings.WindowMaximized = presenter.State == OverlappedPresenterState.Maximized;
        if (!settings.WindowMaximized)
        {
            var dpi = Win32.GetDpiForWindow(_hwnd) / 96.0;
            settings.WindowWidth = (int)Math.Round(AppWindow.Size.Width / dpi);
            settings.WindowHeight = (int)Math.Round(AppWindow.Size.Height / dpi);
        }

        _storage.SaveSettings();
    }

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
        TrayClickCombo.SelectedIndex = _storage.Settings.TrayClickStartsScan ? 1 : 0;
        TrayClickCombo.IsEnabled = _storage.Settings.RunInBackground;
        HistoryToggle.IsOn = _storage.Settings.KeepHistory;
        AutoUpdateToggle.IsOn = _storage.Settings.AutoCheckForUpdates;
        try
        {
            StartupToggle.IsOn = await StartupService.IsEnabledAsync();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            StartupToggle.IsEnabled = false;
        }

        _loadingSettings = false;
        ShowPage(Page.Settings);
    }

    private void OnBackRequested(TitleBar sender, object args) => ShowPage(Page.Home);

    private void ShowPage(Page page)
    {
        if (_page == Page.Pictures && page != Page.Pictures)
        {
            PicturePage.Clear();
        }

        _page = page;
        HomePage.Visibility = page == Page.Home ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == Page.Settings ? Visibility.Visible : Visibility.Collapsed;
        PicturePage.Visibility = page == Page.Pictures ? Visibility.Visible : Visibility.Collapsed;
        CameraPage.Visibility = page == Page.Camera ? Visibility.Visible : Visibility.Collapsed;
        AppTitleBar.IsBackButtonVisible = page != Page.Home;
        UpdateCamera();
    }

    /// <summary>Runs the camera exactly when its page is shown in a visible, non-minimized window.</summary>
    private void UpdateCamera()
    {
        var shouldRun = _page == Page.Camera && AppWindow.IsVisible
            && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        if (shouldRun && !CameraPage.IsRunning)
        {
            _ = CameraPage.StartAsync(_storage);
        }
        else if (!shouldRun && CameraPage.IsRunning)
        {
            _ = CameraPage.StopAsync();
        }
    }

    private void OnCameraClick(object sender, RoutedEventArgs e) => ShowPage(Page.Camera);

    // ---- Pictures (#5) -------------------------------------------------------------------

    /// <summary>Scans pictures and shows the results page.</summary>
    internal async Task OpenPicturesAsync(IReadOnlyList<IImageSource> sources)
    {
        if (sources.Count == 0)
        {
            return;
        }

        ShowPage(Page.Pictures);
        await PicturePage.ShowAsync(sources, _storage);
    }

    /// <summary>Scans picture files given on the command line (--file).</summary>
    public async void OpenPicturePaths(IReadOnlyList<string> paths)
    {
        var sources = new List<IImageSource>();
        foreach (var path in paths)
        {
            try
            {
                sources.Add(await FileImageSource.FromPathAsync(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
                sources.Add(new FailedImageSource(Path.GetFileName(path), ex));
            }
        }

        await OpenPicturesAsync(sources);
    }

    /// <summary>Scans picture files opened with QRSnip from File Explorer (Store version).</summary>
    public void OpenPictureFiles(IReadOnlyList<StorageFile> files) =>
        _ = OpenPicturesAsync(files.Select(f => (IImageSource)new FileImageSource(f)).ToList());

    private async void OnOpenPictureClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.Thumbnail,
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
        };
        foreach (var extension in LaunchArguments.PictureExtensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        InitializeWithWindow.Initialize(picker, _hwnd);
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0)
        {
            await OpenPicturesAsync(files.Select(f => (IImageSource)new FileImageSource(f)).ToList());
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(StandardDataFormats.Bitmap))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Scan for QR codes";
            DropZone.Visibility = Visibility.Visible;
            _lastDragOver = Environment.TickCount64;
        }
    }

    private async void OnDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also bubbles up when the pointer moves between child elements; hide the
        // highlight only if no DragOver follows, i.e. the drag really left the window.
        await Task.Delay(120);
        if (Environment.TickCount64 - _lastDragOver >= 110)
        {
            DropZone.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropZone.Visibility = Visibility.Collapsed;
        var deferral = e.GetDeferral();
        List<IImageSource> sources;
        try
        {
            sources = await PicturesInAsync(e.DataView, "Dropped picture");
        }
        finally
        {
            deferral.Complete();
        }

        if (sources.Count > 0)
        {
            await OpenPicturesAsync(sources);
        }
        else
        {
            Flash("Drop picture files (PNG, JPG, …) to scan them");
        }
    }

    private async void OnPasteInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Bitmap) && !content.Contains(StandardDataFormats.StorageItems))
        {
            return; // nothing to scan; leave the keystroke alone
        }

        args.Handled = true;
        await OpenPicturesAsync(await PicturesInAsync(content, "Pasted picture"));
    }

    /// <summary>Picture files in dropped or pasted data, or the bitmap itself (e.g. from a screenshot).</summary>
    private static async Task<List<IImageSource>> PicturesInAsync(DataPackageView data, string name)
    {
        var sources = new List<IImageSource>();
        if (data.Contains(StandardDataFormats.StorageItems))
        {
            foreach (var item in await data.GetStorageItemsAsync())
            {
                if (item is StorageFile file && LaunchArguments.IsPicturePath(file.Name))
                {
                    sources.Add(new FileImageSource(file));
                }
            }
        }

        if (sources.Count == 0 && data.Contains(StandardDataFormats.Bitmap))
        {
            sources.Add(new StreamImageSource(await data.GetBitmapAsync(), name));
        }

        return sources;
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
        _storage.Settings.AutoCheckForUpdates = AutoUpdateToggle.IsOn;
        _storage.SaveSettings();
        TrayClickCombo.IsEnabled = BackgroundToggle.IsOn;
        App.Current.ApplyBackgroundSetting();
    }

    private void OnTrayClickChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        _storage.Settings.TrayClickStartsScan = TrayClickCombo.SelectedIndex == 1;
        _storage.SaveSettings();
    }

    private void OnQuitClick(object sender, RoutedEventArgs e) => App.Current.Quit();

    private void OnUpdateStateChanged(object? sender, EventArgs e) => ShowUpdate();

    /// <summary>Shows or hides the "Update available" bar for the update found by the last check.</summary>
    private void ShowUpdate()
    {
        var updates = App.Current.Updates;
        if (updates?.Available is not { } update)
        {
            UpdateBar.IsOpen = false;
            return;
        }

        UpdateBar.Severity = InfoBarSeverity.Informational;
        UpdateBar.Title = update.Mandatory ? "Required update" : "Update available";
        UpdateBar.Message = updates.CanInstall
            ? $"QRSnip {update.Version} is ready to install."
            : $"QRSnip {update.Version} is available. Download it to replace this portable copy.";
        UpdateBar.IsClosable = !update.Mandatory;
        UpdateNotes.Text = update.Notes;
        UpdateNotes.Visibility = update.Notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.Content = updates.CanInstall ? "Restart to update" : "Download";
        UpdateBar.IsOpen = true;
    }

    private void OnInstallUpdateClick(object sender, RoutedEventArgs e) => InstallUpdate();

    /// <summary>Downloads and installs the update with progress in the bar (or opens the download page).</summary>
    public async void InstallUpdate()
    {
        if (App.Current.Updates is not { Available: not null } updates || !UpdateButton.IsEnabled)
        {
            return;
        }

        UpdateButton.IsEnabled = false;
        if (updates.CanInstall)
        {
            UpdateProgress.Visibility = Visibility.Visible;
            UpdateProgress.IsIndeterminate = true;
        }

        var progress = new Progress<int>(p =>
        {
            UpdateProgress.IsIndeterminate = false;
            UpdateProgress.Value = p;
        });

        var done = await App.Current.InstallUpdateAsync(progress);
        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateButton.IsEnabled = true;
        if (!done && updates.CanInstall)
        {
            UpdateBar.Severity = InfoBarSeverity.Warning;
            UpdateBar.Message = "The update couldn't be installed right now. Try again later.";
        }
    }

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateRing.IsActive = true;
        UpdateStatus.Text = "Checking for updates…";

        var result = await App.Current.CheckForUpdatesAsync(manual: true);
        UpdateRing.IsActive = false;
        CheckUpdatesButton.IsEnabled = true;
        UpdateStatus.Text = result switch
        {
            UpdateCheckResult.Available => $"QRSnip {App.Current.Updates?.Available?.Version} is available.",
            UpdateCheckResult.UpToDate => "You're up to date.",
            _ => "Couldn't check for updates. Check your internet connection and try again.",
        };
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
