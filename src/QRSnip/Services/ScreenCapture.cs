using System.Runtime.InteropServices;
using QRSnip.Core;
using QRSnip.Native;

namespace QRSnip.Services;

/// <summary>One display: its handle and bounds within the screenshot (image pixels).</summary>
internal sealed record MonitorArea(nint Handle, PixelRect Bounds);

/// <summary>A frozen image of every monitor, positioned in virtual-screen coordinates.</summary>
internal sealed record ScreenShot(BgraImage Image, int Left, int Top)
{
    public IReadOnlyList<MonitorArea> Monitors { get; init; } = [];

    /// <summary>Each monitor's bounds, for scanning them separately (the whole image if unknown).</summary>
    public IReadOnlyList<PixelRect> MonitorRegions => Monitors.Count > 0
        ? Monitors.Select(m => m.Bounds).ToArray()
        : [new PixelRect(0, 0, Image.Width, Image.Height)];
}

internal static class ScreenCapture
{
    private static readonly TimeSpan FallbackTimeout = TimeSpan.FromMilliseconds(800);

    [ThreadStatic]
    private static List<(nint Handle, Win32.RECT Bounds)>? t_monitors;

    /// <summary>
    /// Captures the whole virtual desktop (all monitors) in physical pixels. Monitors that GDI
    /// returns as solid black (hardware-accelerated or full-screen content) are captured again
    /// with Windows.Graphics.Capture.
    /// </summary>
    public static async Task<ScreenShot> CaptureAsync()
    {
        var shot = CaptureVirtualScreen();
        if (!GraphicsCapture.IsSupported)
        {
            return shot;
        }

        foreach (var monitor in shot.Monitors.Where(m => IsBlack(shot.Image, m.Bounds)))
        {
            if (await GraphicsCapture.CaptureMonitorAsync(monitor.Handle, FallbackTimeout) is { } frame)
            {
                Paste(frame, shot.Image, monitor.Bounds);
            }
        }

        return shot;
    }

    /// <summary>Captures the whole virtual desktop (all monitors) with GDI.</summary>
    public static unsafe ScreenShot CaptureVirtualScreen()
    {
        var left = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        var top = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        var width = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
        var height = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);

        var screenDc = Win32.GetDC(0);
        var memoryDc = Win32.CreateCompatibleDC(screenDc);
        var header = new Win32.BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(Win32.BITMAPINFOHEADER),
            biWidth = width,
            biHeight = -height, // top-down rows
            biPlanes = 1,
            biBitCount = 32,
        };

        var bitmap = Win32.CreateDIBSection(screenDc, header, 0, out var bits, 0, 0);
        try
        {
            if (bitmap == 0)
            {
                throw new InvalidOperationException("Could not allocate the screen buffer.");
            }

            var previous = Win32.SelectObject(memoryDc, bitmap);
            Win32.BitBlt(memoryDc, 0, 0, width, height, screenDc, left, top, Win32.SRCCOPY | Win32.CAPTUREBLT);
            Win32.SelectObject(memoryDc, previous);

            var stride = width * 4;
            var pixels = new byte[stride * height];
            new ReadOnlySpan<byte>((void*)bits, pixels.Length).CopyTo(pixels);

            // GDI leaves alpha at 0; XAML treats the buffer as premultiplied, so make it opaque.
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 0xFF;
            }

            var monitors = EnumerateMonitors()
                .Select(m => new MonitorArea(
                    m.Handle,
                    new PixelRect(m.Bounds.Left - left, m.Bounds.Top - top, m.Bounds.Right - m.Bounds.Left, m.Bounds.Bottom - m.Bounds.Top)
                        .ClampTo(width, height)))
                .Where(m => !m.Bounds.IsEmpty)
                .ToArray();

            return new ScreenShot(new BgraImage(pixels, width, height, stride), left, top) { Monitors = monitors };
        }
        finally
        {
            if (bitmap != 0)
            {
                Win32.DeleteObject(bitmap);
            }

            Win32.DeleteDC(memoryDc);
            _ = Win32.ReleaseDC(0, screenDc);
        }
    }

    /// <summary>The bounds of the monitor under the mouse, in virtual-screen pixels.</summary>
    public static Win32.RECT MonitorUnderCursor()
    {
        Win32.GetCursorPos(out var point);
        var monitor = Win32.MonitorFromPoint(point, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new Win32.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfo(monitor, ref info);
        return info.rcMonitor;
    }

    private static unsafe List<(nint Handle, Win32.RECT Bounds)> EnumerateMonitors()
    {
        t_monitors = [];
        try
        {
            Win32.EnumDisplayMonitors(0, 0, &OnMonitor, 0);
            return t_monitors;
        }
        finally
        {
            t_monitors = null;
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe int OnMonitor(nint monitor, nint hdc, Win32.RECT* bounds, nint data)
    {
        t_monitors?.Add((monitor, *bounds));
        return 1;
    }

    /// <summary>True if a sample grid over the area is entirely (near) black.</summary>
    private static bool IsBlack(BgraImage image, PixelRect area)
    {
        var step = Math.Max(8, Math.Min(area.Width, area.Height) / 64);
        for (var y = area.Y + (step / 2); y < area.Y + area.Height; y += step)
        {
            var row = y * image.Stride;
            for (var x = area.X + (step / 2); x < area.X + area.Width; x += step)
            {
                var p = row + (x * 4);
                if (image.Pixels[p] > 12 || image.Pixels[p + 1] > 12 || image.Pixels[p + 2] > 12)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void Paste(BgraImage source, BgraImage target, PixelRect area)
    {
        var width = Math.Min(source.Width, area.Width);
        var height = Math.Min(source.Height, area.Height);
        for (var y = 0; y < height; y++)
        {
            var from = source.Pixels.AsSpan(y * source.Stride, width * 4);
            from.CopyTo(target.Pixels.AsSpan(((area.Y + y) * target.Stride) + (area.X * 4)));
        }

        // Keep the overlay's screenshot opaque, whatever alpha the capture produced.
        for (var y = 0; y < height; y++)
        {
            var row = ((area.Y + y) * target.Stride) + (area.X * 4);
            for (var x = 0; x < width; x++)
            {
                target.Pixels[row + (x * 4) + 3] = 0xFF;
            }
        }
    }
}
