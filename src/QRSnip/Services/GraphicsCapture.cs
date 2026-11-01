using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using QRSnip.Core;
using QRSnip.Native;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace QRSnip.Services;

/// <summary>
/// Captures one monitor with Windows.Graphics.Capture. GDI's BitBlt returns black for some
/// hardware-accelerated or full-screen content (video players, games, some slideshows); the
/// compositor-based capture sees what is actually on screen. Used only as a fallback.
/// </summary>
internal static class GraphicsCapture
{
    private static readonly Guid GraphicsCaptureItemInteropId = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid GraphicsCaptureItemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiDeviceId = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    public static bool IsSupported
    {
        get
        {
            try
            {
                return GraphicsCaptureSession.IsSupported();
            }
            catch (Exception ex) when (ex is COMException or TypeLoadException)
            {
                return false;
            }
        }
    }

    /// <summary>Returns the monitor's pixels, or null if the capture failed or timed out.</summary>
    public static async Task<BgraImage?> CaptureMonitorAsync(nint monitor, TimeSpan timeout)
    {
        IDirect3DDevice? device = null;
        try
        {
            var item = CreateItemForMonitor(monitor);
            device = CreateDevice();
            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
            using var session = pool.CreateCaptureSession(item);

            // Windows 11: no yellow border, no cursor in the picture.
            if (ApiInformation.IsPropertyPresent(typeof(GraphicsCaptureSession).FullName, nameof(GraphicsCaptureSession.IsBorderRequired)))
            {
                TrySet(() => session.IsBorderRequired = false);
            }

            if (ApiInformation.IsPropertyPresent(typeof(GraphicsCaptureSession).FullName, nameof(GraphicsCaptureSession.IsCursorCaptureEnabled)))
            {
                TrySet(() => session.IsCursorCaptureEnabled = false);
            }

            var arrived = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            pool.FrameArrived += (sender, _) =>
            {
                if (sender.TryGetNextFrame() is { } frame && !arrived.TrySetResult(frame))
                {
                    frame.Dispose();
                }
            };

            session.StartCapture();
            using var captured = await arrived.Task.WaitAsync(timeout);
            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(captured.Surface, BitmapAlphaMode.Premultiplied);
            using var bgra = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                ? SoftwareBitmap.Copy(bitmap)
                : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
            bgra.CopyToBuffer(pixels.AsBuffer());
            return new BgraImage(pixels, bgra.PixelWidth, bgra.PixelHeight, bgra.PixelWidth * 4);
        }
        catch (Exception ex) when (ex is COMException or TimeoutException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return null;
        }
        finally
        {
            device?.Dispose();
        }
    }

    private static void TrySet(Action set)
    {
        try
        {
            set();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException)
        {
            // Needs consent or a newer Windows; the capture still works.
        }
    }

    /// <summary>IGraphicsCaptureItemInterop::CreateForMonitor, called through the vtable (trimming-safe).</summary>
    private static unsafe GraphicsCaptureItem CreateItemForMonitor(nint monitor)
    {
        const string ClassName = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(Win32.WindowsCreateString(ClassName, ClassName.Length, out var name));
        nint factory = 0, item = 0;
        try
        {
            Marshal.ThrowExceptionForHR(Win32.RoGetActivationFactory(name, GraphicsCaptureItemInteropId, out factory));
            var vtable = *(nint**)factory;
            var createForMonitor = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)vtable[4];
            var iid = GraphicsCaptureItemId;
            Marshal.ThrowExceptionForHR(createForMonitor(factory, monitor, &iid, &item));
            return GraphicsCaptureItem.FromAbi(item);
        }
        finally
        {
            if (item != 0)
            {
                Marshal.Release(item);
            }

            if (factory != 0)
            {
                Marshal.Release(factory);
            }

            _ = Win32.WindowsDeleteString(name);
        }
    }

    private static IDirect3DDevice CreateDevice()
    {
        var hr = Win32.D3D11CreateDevice(0, Win32.D3D_DRIVER_TYPE_HARDWARE, 0, Win32.D3D11_CREATE_DEVICE_BGRA_SUPPORT, 0, 0, Win32.D3D11_SDK_VERSION, out var d3d, out _, out var context);
        if (hr < 0)
        {
            hr = Win32.D3D11CreateDevice(0, Win32.D3D_DRIVER_TYPE_WARP, 0, Win32.D3D11_CREATE_DEVICE_BGRA_SUPPORT, 0, 0, Win32.D3D11_SDK_VERSION, out d3d, out _, out context);
        }

        Marshal.ThrowExceptionForHR(hr);
        nint dxgi = 0, inspectable = 0;
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, in DxgiDeviceId, out dxgi));
            Marshal.ThrowExceptionForHR(Win32.CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            foreach (var p in new[] { inspectable, dxgi, context, d3d })
            {
                if (p != 0)
                {
                    Marshal.Release(p);
                }
            }
        }
    }
}
