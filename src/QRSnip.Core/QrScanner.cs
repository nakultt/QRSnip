using ZXing;
using ZXing.Common;

namespace QRSnip.Core;

/// <summary>
/// A 32-bit BGRA bitmap in memory (the layout GDI and WinUI both use). Only the colour channels
/// are read when scanning; use <see cref="ImageOps.FlattenOnWhite"/> first for translucent images.
/// </summary>
public sealed record BgraImage(byte[] Pixels, int Width, int Height, int Stride);

/// <summary>A rectangle in image pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public PixelRect ClampTo(int width, int height)
    {
        var x = Math.Clamp(X, 0, width);
        var y = Math.Clamp(Y, 0, height);
        var right = Math.Clamp(X + Width, 0, width);
        var bottom = Math.Clamp(Y + Height, 0, height);
        return new PixelRect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }
}

/// <summary>Finds and decodes 2D codes in screenshots, pictures and camera frames.</summary>
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

    /// <summary>Longest side, in pixels, that large pictures are reduced to when full size finds nothing.</summary>
    private static readonly int[] PictureSizes = [2000, 1000];

    /// <summary>Scans one region of an image (the whole image by default).</summary>
    public static IReadOnlyList<ScanResult> Scan(BgraImage image, PixelRect? region = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        var rect = (region ?? new PixelRect(0, 0, image.Width, image.Height)).ClampTo(image.Width, image.Height);
        if (rect.Width < 8 || rect.Height < 8)
        {
            return [];
        }

        return ScanLuma(ToLuminance(image, rect), rect.Width, rect.Height, rect.X, rect.Y);
    }

    /// <summary>
    /// Scans several regions in parallel, e.g. one per monitor, which is much faster than one
    /// huge bitmap. Results keep their position in the whole image; a code found twice is reported once.
    /// </summary>
    public static IReadOnlyList<ScanResult> Scan(BgraImage image, IReadOnlyList<PixelRect> regions)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(regions);

        var found = new IReadOnlyList<ScanResult>[regions.Count];
        Parallel.For(0, regions.Count, i => found[i] = Scan(image, regions[i]));
        return found.SelectMany(r => r).DistinctBy(r => r.Text).ToList();
    }

    /// <summary>
    /// Scans a picture such as a phone photo. Large pictures are tried at full size and then
    /// reduced (longest side 2000 and 1000 px), which helps with blur and noise.
    /// </summary>
    public static IReadOnlyList<ScanResult> ScanPicture(BgraImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width < 8 || image.Height < 8)
        {
            return [];
        }

        var luma = ToLuminance(image, new PixelRect(0, 0, image.Width, image.Height));
        var results = ScanLuma(luma, image.Width, image.Height, 0, 0);

        var longest = Math.Max(image.Width, image.Height);
        foreach (var size in PictureSizes)
        {
            if (results.Count > 0 || size > longest * 0.8)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var scale = (double)size / longest;
            var (small, w, h) = Downscale(luma, image.Width, image.Height, scale);
            results = Decode(small, w, h, 0, 0, (double)w / image.Width);
        }

        return results;
    }

    private static List<ScanResult> ScanLuma(byte[] luma, int width, int height, int offsetX, int offsetY)
    {
        var results = Decode(luma, width, height, offsetX, offsetY, 1);
        if (results.Count == 0 && Math.Max(width, height) < SmallRegion)
        {
            // Tiny on-screen codes have 1-2 px modules; nearest-neighbour upscaling helps the binarizer.
            var factor = Math.Max(2, SmallRegion / Math.Max(width, height));
            var (scaled, w, h) = Upscale(luma, width, height, factor);
            results = Decode(scaled, w, h, offsetX, offsetY, factor);
        }

        return results;
    }

    /// <param name="scale">Decoded pixels per image pixel, used to map positions back.</param>
    private static List<ScanResult> Decode(byte[] luma, int width, int height, int offsetX, int offsetY, double scale)
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
            .Select(r => new ScanResult(r.Text, r.BarcodeFormat.ToString())
            {
                Corners = CodeOutline.Find(r, luma, width, height)
                    .Select(p => new PixelPoint(offsetX + (p.X / scale), offsetY + (p.Y / scale)))
                    .ToArray(),
            })
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

    /// <summary>Area-averaging reduction; <paramref name="scale"/> is below 1.</summary>
    internal static (byte[] Pixels, int Width, int Height) Downscale(byte[] luma, int width, int height, double scale)
    {
        var w = Math.Max(1, (int)Math.Round(width * scale));
        var h = Math.Max(1, (int)Math.Round(height * scale));
        var output = new byte[w * h];

        // Source column range for each output column, computed once.
        var x0 = new int[w];
        var x1 = new int[w];
        for (var x = 0; x < w; x++)
        {
            x0[x] = (int)((long)x * width / w);
            x1[x] = Math.Max(x0[x] + 1, (int)((long)(x + 1) * width / w));
        }

        var sums = new int[w];
        for (var y = 0; y < h; y++)
        {
            var y0 = (int)((long)y * height / h);
            var y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * height / h));
            Array.Clear(sums);
            for (var sy = y0; sy < y1; sy++)
            {
                var row = sy * width;
                for (var x = 0; x < w; x++)
                {
                    var sum = 0;
                    for (var sx = x0[x]; sx < x1[x]; sx++)
                    {
                        sum += luma[row + sx];
                    }

                    sums[x] += sum;
                }
            }

            var rows = y1 - y0;
            for (var x = 0; x < w; x++)
            {
                output[(y * w) + x] = (byte)(sums[x] / (rows * (x1[x] - x0[x])));
            }
        }

        return (output, w, h);
    }
}
