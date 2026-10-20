using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using QRSnip.Services;
using Windows.ApplicationModel.Activation;

namespace QRSnip;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Lives for the whole process; the shell host is disposed in Quit().")]
public partial class App : Application
{
    private readonly AppActivationArguments _initialActivation;
    private DispatcherQueue? _dispatcher;
    private ShellHost? _shell;
    private MainWindow? _mainWindow;
    private CaptureWindow? _capture;
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

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        _shell = new ShellHost(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        _shell.RegisterHotkey(Storage.Settings.Hotkey);
        _shell.ShowTrayIcon(Storage.Settings.RunInBackground);
        _shell.ScanRequested += (_, _) => StartScan();
        _shell.OpenRequested += (_, _) => ShowMainWindow();
        _shell.ExitRequested += (_, _) => Quit();

        _mainWindow = new MainWindow();
        _mainWindow.CloseRequested += OnMainWindowCloseRequested;

        HandleActivation(_initialActivation, initial: true);
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

    public void ShowMainWindow() => _mainWindow?.ShowAndFocus();

    public async void StartScan()
    {
        if (_capture is not null || _mainWindow is null)
        {
            return;
        }

        // Take the main window out of the picture before freezing the screen.
        var restoreMain = _mainWindow.Visible;
        if (restoreMain)
        {
            _mainWindow.HideInstantly();
            await Task.Delay(60);
        }

        ScreenShot shot;
        try
        {
            shot = ScreenCapture.CaptureVirtualScreen();
        }
        catch (InvalidOperationException)
        {
            if (restoreMain)
            {
                ShowMainWindow();
            }

            return;
        }

        _capture = new CaptureWindow(shot, Storage);
        _capture.Closed += (_, _) =>
        {
            _capture = null;
            if (restoreMain && !_exiting)
            {
                ShowMainWindow();
            }
        };
        _capture.Show();
    }

    public void Quit()
    {
        _exiting = true;
        _capture?.Close();
        _shell?.Dispose();
        _shell = null;
        _mainWindow?.Close();
        Exit();
    }

    /// <summary>Writes unhandled exceptions to %LOCALAPPDATA%\QRSnip\crash.log to make bug reports useful.</summary>
    private static void LogCrash(Exception exception)
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

    private void OnMainWindowCloseRequested(object? sender, EventArgs e)
    {
        if (Storage.Settings.RunInBackground && !_exiting)
        {
            _mainWindow?.Hide();
        }
        else
        {
            Quit();
        }
    }

    private void HandleActivation(AppActivationArguments args, bool initial)
    {
        var commandLine = args.Kind switch
        {
            ExtendedActivationKind.Launch => (args.Data as ILaunchActivatedEventArgs)?.Arguments ?? string.Empty,
            ExtendedActivationKind.StartupTask => "--background",
            _ => string.Empty,
        };

        if (initial)
        {
            commandLine += " " + string.Join(' ', Environment.GetCommandLineArgs().Skip(1));
        }

        if (commandLine.Contains("--scan", StringComparison.OrdinalIgnoreCase))
        {
            StartScan();
        }
        else if (initial && commandLine.Contains("--background", StringComparison.OrdinalIgnoreCase) && Storage.Settings.RunInBackground)
        {
            // Started with Windows: stay in the notification area.
        }
        else
        {
            ShowMainWindow();
        }
    }
}
