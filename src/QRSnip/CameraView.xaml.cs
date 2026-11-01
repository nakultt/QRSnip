using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using QRSnip.Core;
using QRSnip.Services;
using Windows.Foundation;
using Windows.Media.Core;

namespace QRSnip;

/// <summary>
/// Scans QR codes held up to the webcam: live preview with a scan frame, camera picker, mirror
/// and light toggles. The camera runs only while this view is shown and the window is visible.
/// </summary>
public sealed partial class CameraView : UserControl
{
    /// <summary>The same code seen again within this time isn't announced again.</summary>
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(4);

    private readonly HashSet<string> _addedToHistory = [];
    private readonly DispatcherQueueTimer _fade;
    private AppStorage? _storage;
    private CameraScanner? _scanner;
    private MediaSource? _media;
    private IReadOnlyList<CameraDevice> _cameras = [];
    private CameraDevice? _camera;
    private Polygon? _outline;
    private string? _lastText;
    private DateTime _lastSeen;
    private bool _active;
    private bool _loadingPicker;
    private int _generation;

    public CameraView()
    {
        InitializeComponent();
        _fade = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _fade.Interval = TimeSpan.FromMilliseconds(1500);
        _fade.IsRepeating = false;
        _fade.Tick += (_, _) =>
        {
            if (_outline is not null)
            {
                _outline.Opacity = 0;
            }
        };
    }

    public bool IsRunning => _active;

    /// <summary>Turns the camera on (again) and starts reading codes.</summary>
    internal async Task StartAsync(AppStorage storage)
    {
        _storage = storage;
        _active = true;
        _addedToHistory.Clear();
        _lastText = null;
        ResultHost.Visibility = Visibility.Collapsed;
        Guide.Visibility = Visibility.Visible;
        await RestartAsync();
    }

    /// <summary>Turns the camera off immediately (its light goes off too).</summary>
    public async Task StopAsync()
    {
        _active = false;
        _generation++;
        await StopCameraAsync();
    }

    private async Task RestartAsync()
    {
        var generation = ++_generation;
        await StopCameraAsync();
        HideProblem();
        Starting.IsActive = true;
        try
        {
            _cameras = await CameraScanner.FindCamerasAsync();
            if (!_active || generation != _generation)
            {
                return;
            }

            if (_cameras.Count == 0)
            {
                ShowProblem(CameraProblem.NoCamera);
                return;
            }

            // The last camera used, else a rear camera (better for posters), else the first.
            _camera = _cameras.FirstOrDefault(c => c.GroupId == _storage?.Settings.CameraId)
                ?? _cameras.FirstOrDefault(c => c.IsBack)
                ?? _cameras[0];
            _loadingPicker = true;
            CameraPicker.ItemsSource = _cameras.Select(c => c.Name).ToList();
            CameraPicker.SelectedIndex = IndexOf(_camera);
            CameraPicker.Visibility = _cameras.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            _loadingPicker = false;

            // Front and most external webcams look like a mirror; rear cameras don't.
            MirrorButton.IsChecked = !_camera.IsBack;
            ApplyMirror();

            var scanner = new CameraScanner();
            var problem = await scanner.StartAsync(_camera);
            if (!_active || generation != _generation || problem != CameraProblem.None)
            {
                await scanner.DisposeAsync();
                if (_active && generation == _generation)
                {
                    ShowProblem(problem);
                }

                return;
            }

            _scanner = scanner;
            scanner.Detected += OnDetected;
            scanner.Lost += OnLost;
            _media = MediaSource.CreateFromMediaFrameSource(scanner.Source);
            Preview.Source = _media;
            if (Preview.MediaPlayer is { } player)
            {
                player.RealTimePlayback = true;
            }

            TorchButton.IsChecked = false;
            TorchButton.Visibility = scanner.TorchSupported ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            if (generation == _generation)
            {
                ShowProblem(ex is UnauthorizedAccessException ? CameraProblem.AccessDenied : CameraProblem.Failed);
            }
        }
        finally
        {
            if (generation == _generation)
            {
                Starting.IsActive = false;
            }
        }
    }

    private async Task StopCameraAsync()
    {
        _fade.Stop();
        if (_outline is not null)
        {
            _outline.Opacity = 0;
        }

        Preview.Source = null;
        _media?.Dispose();
        _media = null;

        if (_scanner is { } scanner)
        {
            _scanner = null;
            scanner.Detected -= OnDetected;
            scanner.Lost -= OnLost;
            await scanner.DisposeAsync();
        }
    }

    private int IndexOf(CameraDevice camera)
    {
        for (var i = 0; i < _cameras.Count; i++)
        {
            if (_cameras[i].GroupId == camera.GroupId)
            {
                return i;
            }
        }

        return 0;
    }

    private void OnDetected(object? sender, CameraDetection detection) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(sender, _scanner))
            {
                Show(detection);
            }
        });

    private void OnLost(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (ReferenceEquals(sender, _scanner))
            {
                await StopCameraAsync();
                ShowProblem(CameraProblem.InUse);
            }
        });

    private void Show(CameraDetection detection)
    {
        var result = detection.Results[0];
        Highlight(result, detection.FrameWidth, detection.FrameHeight);

        var now = DateTime.UtcNow;
        var repeat = result.Text == _lastText && now - _lastSeen < RepeatWindow;
        _lastSeen = now;
        if (repeat)
        {
            return;
        }

        _lastText = result.Text;
        Result.Entry = new HistoryEntry { Text = result.Text, ScannedAt = DateTimeOffset.Now };
        ResultHost.Visibility = Visibility.Visible;
        Guide.Visibility = Visibility.Collapsed;

        if (_addedToHistory.Add(result.Text))
        {
            _storage?.AddToHistory([result]);
        }

        if (_storage?.Settings.AutoCopy == true)
        {
            ResultPresenter.Copy(result.Text);
            Flash("Copied to clipboard");
        }
    }

    /// <summary>Briefly outlines the code on the preview (mapped through the letterboxing and mirroring).</summary>
    private void Highlight(ScanResult result, int frameWidth, int frameHeight)
    {
        if (result.Corners.Count < 3 || frameWidth <= 0 || frameHeight <= 0)
        {
            return;
        }

        var (width, height) = (PreviewHost.ActualWidth, PreviewHost.ActualHeight);
        var scale = Math.Min(width / frameWidth, height / frameHeight);
        var offsetX = (width - (frameWidth * scale)) / 2;
        var offsetY = (height - (frameHeight * scale)) / 2;
        var mirrored = MirrorButton.IsChecked == true;

        var points = new PointCollection();
        foreach (var c in result.Corners)
        {
            var x = offsetX + (c.X * scale);
            points.Add(new Point(mirrored ? width - x : x, offsetY + (c.Y * scale)));
        }

        if (_outline is null)
        {
            _outline = new Polygon
            {
                Stroke = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                StrokeThickness = 4,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            };
            Highlight.Children.Add(_outline);
        }

        _outline.Points = points;
        _outline.Opacity = 1;
        _fade.Stop();
        _fade.Start();
    }

    private void ShowProblem(CameraProblem problem)
    {
        var (glyph, title, detail) = problem switch
        {
            CameraProblem.NoCamera => ("", "No camera found", "Connect a webcam, then try again."),
            CameraProblem.AccessDenied => ("", "Camera access is turned off", "Allow apps (and desktop apps) to use the camera in Windows Settings › Privacy & security › Camera."),
            CameraProblem.InUse => ("", "The camera is in use by another app", "Close the other app (for example Teams or the Camera app), then try again."),
            _ => ("", "The camera couldn't start", "Try again, or pick another camera."),
        };

        ProblemIcon.Glyph = glyph;
        ProblemTitle.Text = title;
        ProblemDetail.Text = detail;
        SettingsLink.Visibility = problem == CameraProblem.AccessDenied ? Visibility.Visible : Visibility.Collapsed;
        Problem.Visibility = Visibility.Visible;
        ScanFrame.Visibility = Visibility.Collapsed;
        TorchButton.Visibility = Visibility.Collapsed;
    }

    private void HideProblem()
    {
        Problem.Visibility = Visibility.Collapsed;
        ScanFrame.Visibility = Visibility.Visible;
    }

    private void ApplyMirror() =>
        Preview.RenderTransform = new ScaleTransform { ScaleX = MirrorButton.IsChecked == true ? -1 : 1 };

    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var side = Math.Min(e.NewSize.Width, e.NewSize.Height) * 0.6;
        ScanFrame.Width = ScanFrame.Height = Math.Max(0, side);
    }

    private async void OnCameraChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPicker || CameraPicker.SelectedIndex < 0 || CameraPicker.SelectedIndex >= _cameras.Count || _storage is null)
        {
            return;
        }

        _storage.Settings.CameraId = _cameras[CameraPicker.SelectedIndex].GroupId;
        _storage.SaveSettings();
        await RestartAsync();
    }

    private void OnMirrorClick(object sender, RoutedEventArgs e) => ApplyMirror();

    private void OnTorchClick(object sender, RoutedEventArgs e) => _scanner?.SetTorch(TorchButton.IsChecked == true);

    private async void OnRetryClick(object sender, RoutedEventArgs e)
    {
        if (_active)
        {
            await RestartAsync();
        }
    }

    private async void OnPrivacySettingsClick(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-webcam"));

    private async void OnResultAction(object? sender, ResultActionEventArgs e)
    {
        switch (e.Action)
        {
            case ResultAction.Open:
                await ResultPresenter.OpenAsync(e.Entry.Content);
                break;
            case ResultAction.Copy:
                ResultPresenter.Copy(e.Entry.Text);
                Flash("Copied to clipboard");
                break;
            case ResultAction.CopySecret when e.Entry.Content.Secret is { } secret:
                ResultPresenter.Copy(secret);
                Flash("Password copied");
                break;
        }
    }

    private async void Flash(string message)
    {
        Status.Text = message;
        await Task.Delay(1800);
        if (Status.Text == message)
        {
            Status.Text = string.Empty;
        }
    }
}
