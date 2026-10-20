using QRSnip.Core;
using QRSnip.Native;

namespace QRSnip.Services;

/// <summary>A frozen image of every monitor, positioned in virtual-screen coordinates.</summary>
internal sealed record ScreenShot(BgraImage Image, int Left, int Top);

internal static class ScreenCapture
{
    /// <summary>Captures the whole virtual desktop (all monitors) in physical pixels.</summary>
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

            return new ScreenShot(new BgraImage(pixels, width, height, stride), left, top);
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
        var info = new Win32.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfo(monitor, ref info);
        return info.rcMonitor;
    }
}
