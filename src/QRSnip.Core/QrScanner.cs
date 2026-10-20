using ZXing;
using ZXing.Common;

namespace QRSnip.Core;

/// <summary>A 32-bit BGRA bitmap in memory (the layout GDI and WinUI both use).</summary>
public sealed record BgraImage(byte[] Pixels, int Width, int Height, int Stride);

/// <summary>A rectangle in image pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public PixelRect ClampTo(int width, int height)
    {
        var x = Math.Clamp(X, 0, width);
        var y = Math.Clamp(Y, 0, height);
        var right = Math.Clamp(X + Width, 0, width);
        var bottom = Math.Clamp(Y + Height, 0, height);
        return new PixelRect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }
}

/// <summary>Finds and decodes 2D codes in a region of a screenshot.</summary>
public static class QrScanner
{
    private static readonly BarcodeFormat[] Formats =
    [
        BarcodeFormat.QR_CODE,
        BarcodeFormat.DATA_MATRIX,
        BarcodeFormat.AZTEC,
        BarcodeFormat.PDF_417,
    ];

    /// <summary>Codes smaller than this (in pixels) are upscaled before a second attempt.</summary>
    private const int SmallRegion = 480;

    public static IReadOnlyList<ScanResult> Scan(BgraImage image, PixelRect? region = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        var rect = (region ?? new PixelRect(0, 0, image.Width, image.Height)).ClampTo(image.Width, image.Height);
        if (rect.Width < 8 || rect.Height < 8)
        {
            return [];
        }

        var luma = ToLuminance(image, rect);

        var results = Decode(luma, rect.Width, rect.Height);
        if (results.Count == 0 && Math.Max(rect.Width, rect.Height) < SmallRegion)
        {
            // Tiny on-screen codes have 1-2 px modules; nearest-neighbour upscaling helps the binarizer.
            var factor = Math.Max(2, SmallRegion / Math.Max(rect.Width, rect.Height));
            var (scaled, w, h) = Upscale(luma, rect.Width, rect.Height, factor);
            results = Decode(scaled, w, h);
        }

        return results;
    }

    private static List<ScanResult> Decode(byte[] luma, int width, int height)
    {
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                PossibleFormats = Formats,
            },
        };

        var source = new RGBLuminanceSource(luma, width, height, RGBLuminanceSource.BitmapFormat.Gray8);
        var decoded = reader.DecodeMultiple(source) ?? [];

        return decoded
            .Where(r => !string.IsNullOrEmpty(r.Text))
            .DistinctBy(r => r.Text)
            .Select(r => new ScanResult(r.Text, r.BarcodeFormat.ToString()))
            .ToList();
    }

    internal static byte[] ToLuminance(BgraImage image, PixelRect rect)
    {
        var luma = new byte[rect.Width * rect.Height];
        var src = image.Pixels;
        for (var y = 0; y < rect.Height; y++)
        {
            var row = ((rect.Y + y) * image.Stride) + (rect.X * 4);
            var dst = y * rect.Width;
            for (var x = 0; x < rect.Width; x++)
            {
                var p = row + (x * 4);
                // ITU-R BT.601 weights in fixed point: B, G, R.
                luma[dst + x] = (byte)(((src[p] * 29) + (src[p + 1] * 150) + (src[p + 2] * 77)) >> 8);
            }
        }

        return luma;
    }

    private static (byte[] Pixels, int Width, int Height) Upscale(byte[] luma, int width, int height, int factor)
    {
        var w = width * factor;
        var h = height * factor;
        var output = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            var srcRow = (y / factor) * width;
            var dstRow = y * w;
            for (var x = 0; x < w; x++)
            {
                output[dstRow + x] = luma[srcRow + (x / factor)];
            }
        }

        return (output, w, h);
    }
}
