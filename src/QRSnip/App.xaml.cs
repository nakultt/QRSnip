using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using QRSnip.Core;
using QRSnip.Native;
using QRSnip.Services;
using Windows.ApplicationModel.Activation;

namespace QRSnip;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Lives for the whole process; the shell host is disposed in Quit().")]
public partial class App : Application
{
    /// <summary>How long the main window may stay hidden before its visual tree is released.</summary>
    private static readonly TimeSpan ReleaseWindowAfter = TimeSpan.FromMinutes(3);

    private readonly AppActivationArguments _initialActivation;
    private DispatcherQueue? _dispatcher;
    private ShellHost? _shell;
    private NotificationService? _notifications;
    private TrayMenu? _trayMenu;
    private MainWindow? _mainWindow;
    private CaptureWindow? _capture;
    private DispatcherQueueTimer? _idleTimer;
    private DispatcherQueueTimer? _updateTimer;
    private UpdateService? _updates;
    private DateTimeOffset _mainHiddenAt;
    private bool _exiting;

    public App(AppInstance instance, AppActivationArguments activation)
    {
        _initialActivation = activation;
        InitializeComponent();
        instance.Activated += (_, args) => _dispatcher?.TryEnqueue(() => HandleActivation(args, initial: false));
        UnhandledException += (_, e) => LogCrash(e.Exception);
#if DEBUG
        DebugSettings.XamlResourceReferenceFailed += (_, e) => LogCrash(new InvalidOperationException(e.Message));
        DebugSettings.BindingFailed += (_, e) => LogCrash(new InvalidOperationException(e.Message));
#endif
    }

    public static new App Current => (App)Application.Current;

    public AppStorage Storage { get; } = new();

    public string? Hotkey => _shell?.Hotkey;

    public string? FullScreenHotkey => _shell?.FullScreenHotkey;

    internal UpdateService? Updates => _updates;

    /// <summary>An update check finished or an update became available.</summary>
    public event EventHandler? UpdateStateChanged;

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        _notifications = new NotificationService(_dispatcher, () => _shell);
        _notifications.Invoked += (_, action) => HandleNotificationAction(action);
        _notifications.Register();

        _shell = new ShellHost(Path.Combine(AppContext.BaseDirectory, "Assets"));
        _shell.RegisterHotkey(Storage.Settings.Hotkey);
        _shell.RegisterFullScreenHotkey(Storage.Settings.FullScreenHotkey);
        _shell.ShowTrayIcon(Storage.Settings.RunInBackground);
        _shell.ScanRequested += (_, _) => StartScan();
        _shell.FullScreenScanRequested += (_, _) => StartScan(ScanMode.FullScreen);
        _shell.TrayClicked += (_, _) => OnTrayClicked();
        _shell.MenuRequested += (_, pt) => ShowTrayMenu(pt);
        _shell.OpenRequested += (_, _) => ShowMainWindow();
        _shell.ExitRequested += (_, _) => Quit();

        _idleTimer = _dispatcher.CreateTimer();
        _idleTimer.Interval = ReleaseWindowAfter;
        _idleTimer.IsRepeating = false;
        _idleTimer.Tick += (_, _) => ReleaseIdleResources();

        // Automatic update checks: shortly after start, then hourly (each one checks at most once a day).
        _updates = new UpdateService(() => WinRT.Interop.WindowNative.GetWindowHandle(EnsureMainWindow()));
        _updateTimer = _dispatcher.CreateTimer();
        _updateTimer.Interval = TimeSpan.FromSeconds(45);
        _updateTimer.IsRepeating = true;
        _updateTimer.Tick += (timer, _) =>
        {
            timer.Interval = TimeSpan.FromHours(1);
            _ = CheckForUpdatesAsync(manual: false);
        };
        _updateTimer.Start();

        HandleActivation(_initialActivation, initial: true);
    }

    /// <summary>
    /// Looks for a new version. Automatic checks run at most once a day, never during a scan,
    /// and only when turned on; failures are silent (manual checks report them).
    /// </summary>
    internal async Task<UpdateCheckResult> CheckForUpdatesAsync(bool manual)
    {
        if (_updates is null || _exiting)
        {
            return UpdateCheckResult.Failed;
        }

        if (!manual)
        {
            var last = Storage.Settings.LastUpdateCheck;
            if (!Storage.Settings.AutoCheckForUpdates || _capture is not null
                || (last is { } at && DateTimeOffset.UtcNow - at < TimeSpan.FromDays(1)))
            {
                return UpdateCheckResult.UpToDate;
            }
        }

        var result = await _updates.CheckAsync();
        if (result != UpdateCheckResult.Failed)
        {
            Storage.Settings.LastUpdateCheck = DateTimeOffset.UtcNow;
            Storage.SaveSettings();
        }

        UpdateStateChanged?.Invoke(this, EventArgs.Empty);

        // In the tray: tell people once per version with a Windows notification.
        if (result == UpdateCheckResult.Available && _updates.Available is { } update && !manual
            && _mainWindow?.Visible != true && Storage.Settings.NotifiedUpdateVersion != update.Version)
        {
            Storage.Settings.NotifiedUpdateVersion = update.Version;
            Storage.SaveSettings();
            var firstLine = update.Notes.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(l => l.StartsWith('•'));
            _notifications?.Show(
                $"QRSnip {update.Version} is available",
                firstLine?.TrimStart('•', ' ') ?? "Restart QRSnip to get the latest version.",
                _updates.CanInstall ? "Restart to update" : "Download",
                UpdateAction);
        }

        return result;
    }

    /// <summary>Installs the available update and restarts (or opens the download page for portable copies).</summary>
    public async Task<bool> InstallUpdateAsync(IProgress<int>? progress)
    {
        if (_updates is null || _capture is not null || _exiting)
        {
            return false;
        }

        return await _updates.InstallAsync(progress, _ => PrepareForRestart(), restartInBackground: _mainWindow?.Visible != true);
    }

    /// <summary>Called when the user changes "Keep running in the background".</summary>
    public void ApplyBackgroundSetting() => _shell?.ShowTrayIcon(Storage.Settings.RunInBackground);

    /// <summary>Re-registers the scan shortcut and returns the one actually in effect.</summary>
    public string? ChangeHotkey(string preferred)
    {
        Storage.Settings.Hotkey = preferred;
        Storage.SaveSettings();
        return _shell?.RegisterHotkey(preferred);
    }

    /// <summary>Re-registers the full screen shortcut and returns the one actually in effect (null: none).</summary>
    public string? ChangeFullScreenHotkey(string preferred)
    {
        Storage.Settings.FullScreenHotkey = preferred;
        Storage.SaveSettings();
        return _shell?.RegisterFullScreenHotkey(preferred);
    }

    public void ShowMainWindow()
    {
        _idleTimer?.Stop();
        EnsureMainWindow().ShowAndFocus();
    }

    /// <summary>Opens the capture overlay, in the last used mode unless one is given.</summary>
    public async void StartScan(ScanMode? mode = null)
    {
        if (_capture is not null || _exiting)
        {
            return;
        }

        var scanMode = mode ?? Storage.Settings.LastScanMode;

        // Take the main window out of the picture before freezing the screen.
        var restoreMain = _mainWindow?.Visible == true;
        if (restoreMain)
        {
            _mainWindow!.HideInstantly();
            await Task.Delay(60);
        }

        ScreenShot shot;
        try
        {
            shot = await ScreenCapture.CaptureAsync();
        }
        catch (InvalidOperationException)
        {
            if (restoreMain)
            {
                ShowMainWindow();
            }

            return;
        }

        if (_capture is not null || _exiting)
        {
            return; // another scan started while capturing
        }

        _capture = new CaptureWindow(shot, Storage, scanMode);
        _capture.Closed += (_, _) =>
        {
            _capture = null;
            if (restoreMain && !_exiting)
            {
                ShowMainWindow();
            }
            else
            {
                // Back in the tray: hand the screenshot's memory back soon.
                ScheduleIdleRelease(TimeSpan.FromSeconds(5));
            }
        };
        _capture.Show();
    }

    /// <summary>Stops QRSnip completely: shortcut, notification-area icon and process.</summary>
    public void Quit()
    {
        _exiting = true;
        _updateTimer?.Stop();
        _idleTimer?.Stop();
        _capture?.Close();
        _trayMenu?.Close();
        _trayMenu = null;
        _shell?.Dispose();
        _shell = null;
        _mainWindow?.Release();
        _mainWindow = null;
        Exit();
    }

    /// <summary>Writes unhandled exceptions to %LOCALAPPDATA%\QRSnip\crash.log to make bug reports useful.</summary>
    internal static void LogCrash(Exception exception)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QRSnip");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "crash.log"), $"[{DateTimeOffset.Now:O}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    private const string UpdateAction = "update";

    /// <summary>The updater restarts the process itself; release the shortcut and tray icon first.</summary>
    private void PrepareForRestart()
    {
        _exiting = true;
        _updateTimer?.Stop();
        _idleTimer?.Stop();
        _capture?.Close();
        _trayMenu?.Close();
        _trayMenu = null;
        _shell?.Dispose();
        _shell = null;
    }

    private MainWindow EnsureMainWindow()
    {
        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow();
            _mainWindow.CloseRequested += OnMainWindowCloseRequested;
        }

        return _mainWindow;
    }

    private void OnMainWindowCloseRequested(object? sender, EventArgs e)
    {
        if (!Storage.Settings.RunInBackground || _exiting)
        {
            Quit();
            return;
        }

        _mainWindow?.Hide();
        _mainHiddenAt = DateTimeOffset.UtcNow;
        ScheduleIdleRelease(ReleaseWindowAfter);

        if (!Storage.Settings.ShownBackgroundNotice)
        {
            // Tell people once where QRSnip went, so they don't think it quit.
            Storage.Settings.ShownBackgroundNotice = true;
            Storage.SaveSettings();
            var how = Hotkey is { } key ? $"Press {key} to scan" : "Scan from its icon";
            _notifications?.Show(
                "QRSnip is still running",
                $"{how}, or find QRSnip in the notification area. You can change this in Settings.");
        }
    }

    private void OnTrayClicked()
    {
        if (Storage.Settings.TrayClickStartsScan)
        {
            StartScan();
        }
        else
        {
            ShowMainWindow();
        }
    }

    private void ShowTrayMenu(Win32.POINT at)
    {
        _shell?.RefreshIcon();
        try
        {
            _trayMenu ??= new TrayMenu();
            _trayMenu.Show(at, BuildTrayMenu());
        }
        catch (Exception ex)
        {
            // Never leave people without a way to exit: fall back to the classic menu.
            LogCrash(ex);
            _trayMenu = null;
            _shell?.ShowClassicMenu(at);
        }
    }

    private List<MenuFlyoutItemBase> BuildTrayMenu()
    {
        var items = new List<MenuFlyoutItemBase>
        {
            MenuItem("Scan QR code", "", () => StartScan(), Hotkey),
            MenuItem("Scan full screen", "", () => StartScan(ScanMode.FullScreen), FullScreenHotkey),
            new MenuFlyoutSeparator(),
        };

        // The last three results, each with its actions.
        var recent = Storage.History.Take(3).ToList();
        foreach (var entry in recent)
        {
            var title = entry.Content.Title.Length > 40 ? entry.Content.Title[..39] + "…" : entry.Content.Title;
            var sub = new MenuFlyoutSubItem { Text = title, Icon = new FontIcon { Glyph = entry.Glyph } };
            if (entry.CanOpen)
            {
                sub.Items.Add(MenuItem(entry.OpenLabel, "", () => _ = ResultPresenter.OpenAsync(entry.Content)));
            }

            sub.Items.Add(MenuItem("Copy", "", () => ResultPresenter.Copy(entry.Text)));
            if (entry.Content.Secret is { Length: > 0 } secret)
            {
                sub.Items.Add(MenuItem("Copy password", "", () => ResultPresenter.Copy(secret)));
            }

            items.Add(sub);
        }

        if (recent.Count > 0)
        {
            items.Add(new MenuFlyoutSeparator());
        }

        items.Add(MenuItem("Open QRSnip", "", ShowMainWindow));
        items.Add(MenuItem("Exit", "", Quit));
        return items;

        static MenuFlyoutItem MenuItem(string text, string glyph, Action action, string? shortcut = null)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            if (shortcut is not null)
            {
                item.KeyboardAcceleratorTextOverride = shortcut;
            }

            item.Click += (_, _) => action();
            return item;
        }
    }

    private void HandleNotificationAction(string action)
    {
        switch (action)
        {
            case UpdateAction:
                ShowMainWindow();
                _mainWindow?.InstallUpdate();
                break;
            default:
                ShowMainWindow();
                break;
        }
    }

    private void ScheduleIdleRelease(TimeSpan after)
    {
        if (_idleTimer is null || _exiting)
        {
            return;
        }

        _idleTimer.Stop();
        _idleTimer.Interval = after;
        _idleTimer.Start();
    }

    /// <summary>
    /// While QRSnip waits in the tray, drop the hidden main window's visual tree (it is rebuilt
    /// on demand) and give memory back to Windows, keeping the idle footprint small.
    /// </summary>
    private void ReleaseIdleResources()
    {
        if (_exiting || _capture is not null || _trayMenu?.IsOpen == true)
        {
            return;
        }

        if (_mainWindow is { Visible: false } hidden)
        {
            var hiddenFor = DateTimeOffset.UtcNow - _mainHiddenAt;
            if (hiddenFor >= ReleaseWindowAfter)
            {
                _mainWindow = null;
                hidden.CloseRequested -= OnMainWindowCloseRequested;
                hidden.Release();
            }
            else
            {
                ScheduleIdleRelease(ReleaseWindowAfter - hiddenFor);
            }
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        Win32.EmptyWorkingSet(Win32.GetCurrentProcess());
    }

    private void HandleActivation(AppActivationArguments args, bool initial)
    {
        if (args.Kind == ExtendedActivationKind.AppNotification && args.Data is AppNotificationActivatedEventArgs notification)
        {
            HandleNotificationAction(NotificationService.ActionOf(notification));
            return;
        }

        var launch = args.Kind switch
        {
            ExtendedActivationKind.Launch => LaunchArguments.Parse((args.Data as ILaunchActivatedEventArgs)?.Arguments),
            ExtendedActivationKind.StartupTask => LaunchArguments.Parse(["--background"]),
            _ => LaunchArguments.Empty,
        };

        if (initial)
        {
            launch = launch.Merge(LaunchArguments.Parse(Environment.GetCommandLineArgs().Skip(1)));
        }

        if (launch.FullScreen)
        {
            StartScan(ScanMode.FullScreen);
        }
        else if (launch.Scan)
        {
            StartScan();
        }
        else if (initial && launch.Background && Storage.Settings.RunInBackground)
        {
            // Started with Windows: stay in the notification area.
        }
        else
        {
            ShowMainWindow();
        }
    }
}
