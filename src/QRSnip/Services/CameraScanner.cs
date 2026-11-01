using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using QRSnip.Core;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace QRSnip.Services;

/// <summary>A connected camera.</summary>
internal sealed record CameraDevice(string GroupId, string Name, bool IsFront, bool IsBack);

/// <summary>Why the camera couldn't start.</summary>
internal enum CameraProblem
{
    None,
    NoCamera,
    AccessDenied,
    InUse,
    Failed,
}

/// <summary>Codes seen in one camera frame, with the frame size their corners refer to.</summary>
internal sealed record CameraDetection(IReadOnlyList<ScanResult> Results, int FrameWidth, int FrameHeight);

/// <summary>
/// Reads frames from a camera with <see cref="MediaFrameReader"/> and decodes them off the UI
/// thread at up to about 10 frames per second. Frames that arrive while a decode is running are
/// dropped. Frames are only held in memory, never saved. Dispose to turn the camera off.
/// </summary>
internal sealed class CameraScanner : IImageSource, IAsyncDisposable
{
    private static readonly TimeSpan MinFrameInterval = TimeSpan.FromMilliseconds(110);

    private readonly CancellationTokenSource _stop = new();
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private TaskCompletionSource<BgraImage>? _waiting;
    private Task? _loop;

    public string Name => "Camera";

    /// <summary>The colour stream, for the live preview.</summary>
    public MediaFrameSource? Source { get; private set; }

    public bool TorchSupported => _capture?.VideoDeviceController.TorchControl.Supported == true;

    /// <summary>Codes were found in a frame (raised on a background thread).</summary>
    public event EventHandler<CameraDetection>? Detected;

    /// <summary>The camera stopped working while running, e.g. it was unplugged or taken over.</summary>
    public event EventHandler? Lost;

    public static async Task<IReadOnlyList<CameraDevice>> FindCamerasAsync()
    {
        var cameras = new List<CameraDevice>();
        foreach (var group in await MediaFrameSourceGroup.FindAllAsync())
        {
            var color = group.SourceInfos.FirstOrDefault(i => i.SourceKind == MediaFrameSourceKind.Color);
            if (color is null)
            {
                continue;
            }

            var panel = color.DeviceInformation?.EnclosureLocation?.Panel;
            cameras.Add(new CameraDevice(group.Id, group.DisplayName, panel == Panel.Front, panel == Panel.Back));
        }

        return cameras;
    }

    public void SetTorch(bool on)
    {
        if (TorchSupported)
        {
            _capture!.VideoDeviceController.TorchControl.Enabled = on;
        }
    }

    /// <summary>Starts the camera (exclusively if possible, else shared with the app using it) and the decode loop.</summary>
    public async Task<CameraProblem> StartAsync(CameraDevice camera)
    {
        var group = await MediaFrameSourceGroup.FromIdAsync(camera.GroupId);
        if (group is null)
        {
            return CameraProblem.NoCamera;
        }

        var problem = await TryStartAsync(group, MediaCaptureSharingMode.ExclusiveControl);
        if (problem == CameraProblem.InUse)
        {
            // Another app has the camera; Windows can often still share its frames.
            problem = await TryStartAsync(group, MediaCaptureSharingMode.SharedReadOnly);
        }

        if (problem == CameraProblem.None)
        {
            _loop = Task.Run(() => DecodeLoopAsync(_stop.Token));
        }

        return problem;
    }

    /// <summary>The newest frame: waits for the next one to arrive.</summary>
    public Task<BgraImage> GetImageAsync(CancellationToken cancellationToken = default)
    {
        var waiting = new TaskCompletionSource<BgraImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _waiting, waiting)?.TrySetCanceled(cancellationToken);
        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(() => waiting.TrySetCanceled(cancellationToken));
            _ = waiting.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        return waiting.Task;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        Interlocked.Exchange(ref _waiting, null)?.TrySetCanceled();
        if (_reader is { } reader)
        {
            reader.FrameArrived -= OnFrameArrived;
            try
            {
                await reader.StopAsync();
            }
            catch (Exception ex) when (ex is COMException or ObjectDisposedException or InvalidOperationException)
            {
            }

            reader.Dispose();
            _reader = null;
        }

        if (_capture is { } capture)
        {
            capture.Failed -= OnFailed;
            capture.Dispose(); // turns the camera (and its light) off
            _capture = null;
        }

        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    private async Task<CameraProblem> TryStartAsync(MediaFrameSourceGroup group, MediaCaptureSharingMode sharing)
    {
        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = sharing,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                StreamingCaptureMode = StreamingCaptureMode.Video,
            });

            var source = capture.FrameSources.Values
                .Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
                .OrderBy(s => s.Info.MediaStreamType == MediaStreamType.VideoPreview ? 0 : 1)
                .FirstOrDefault();
            if (source is null)
            {
                capture.Dispose();
                return CameraProblem.NoCamera;
            }

            if (sharing == MediaCaptureSharingMode.ExclusiveControl && PickFormat(source) is { } format)
            {
                await source.SetFormatAsync(format);
            }

            var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            reader.FrameArrived += OnFrameArrived;
            var status = await reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                reader.FrameArrived -= OnFrameArrived;
                reader.Dispose();
                capture.Dispose();
                return status is MediaFrameReaderStartStatus.ExclusiveControlNotAvailable or MediaFrameReaderStartStatus.DeviceNotAvailable
                    ? CameraProblem.InUse
                    : CameraProblem.Failed;
            }

            capture.Failed += OnFailed;
            (_capture, _reader, Source) = (capture, reader, source);
            return CameraProblem.None;
        }
        catch (UnauthorizedAccessException)
        {
            capture.Dispose();
            return CameraProblem.AccessDenied;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            capture.Dispose();
            return IsInUse(ex.HResult) ? CameraProblem.InUse : CameraProblem.Failed;
        }
    }

    /// <summary>About 720p is plenty for QR codes and keeps decoding fast.</summary>
    private static MediaFrameFormat? PickFormat(MediaFrameSource source) => source.SupportedFormats
        .Where(f => f.VideoFormat.Width > 0 && f.FrameRate.Denominator > 0 && (double)f.FrameRate.Numerator / f.FrameRate.Denominator >= 14)
        .OrderBy(f => Math.Abs(((long)f.VideoFormat.Width * f.VideoFormat.Height) - (1280 * 720)))
        .FirstOrDefault();

    private static bool IsInUse(int hresult) => unchecked((uint)hresult) is
        0xC00D3704 or // MF_E_HW_MFT_FAILED_START_STREAMING
        0xC00DABE4 or // MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED
        0xC00DABE5 or // MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED
        0x80070020 or // ERROR_SHARING_VIOLATION
        0x800700AA;   // ERROR_BUSY

    private async Task DecodeLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            BgraImage frame;
            try
            {
                frame = await GetImageAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var results = QrScanner.Scan(frame);
            if (results.Count > 0)
            {
                Detected?.Invoke(this, new CameraDetection(results, frame.Width, frame.Height));
            }

            var rest = MinFrameInterval - (DateTime.UtcNow - started);
            if (rest > TimeSpan.Zero)
            {
                await Task.Delay(rest, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        // Only convert a frame when the decode loop is waiting for one; others are dropped.
        if (Volatile.Read(ref _waiting) is null)
        {
            return;
        }

        using var frame = sender.TryAcquireLatestFrame();
        var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (bitmap is null)
        {
            return;
        }

        using var bgra = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
            ? SoftwareBitmap.Copy(bitmap)
            : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8);
        var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyToBuffer(pixels.AsBuffer());
        Interlocked.Exchange(ref _waiting, null)?.TrySetResult(new BgraImage(pixels, bgra.PixelWidth, bgra.PixelHeight, bgra.PixelWidth * 4));
    }

    private void OnFailed(MediaCapture sender, MediaCaptureFailedEventArgs args) => Lost?.Invoke(this, EventArgs.Empty);
}
