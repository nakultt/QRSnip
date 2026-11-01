using StbImageSharp;
using ZXing;

namespace QRSnip.Core.Tests;

/// <summary>Builds synthetic screenshots and loads the sample pictures.</summary>
internal static class TestImages
{
    public static BgraImage Canvas(int width, int height, byte shade = 0xF0)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        Array.Fill(pixels, shade);
        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 0xFF;
        }

        return new BgraImage(pixels, width, height, stride);
    }

    /// <summary>Draws a QR code and returns the bounds of the code itself (without quiet zone).</summary>
    public static PixelRect Stamp(BgraImage image, string text, int x, int y, int size, bool invert = false)
    {
        var matrix = new MultiFormatWriter().encode(
            text,
            BarcodeFormat.QR_CODE,
            size,
            size,
            new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 1 });

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var my = 0; my < matrix.Height; my++)
        {
            for (var mx = 0; mx < matrix.Width; mx++)
            {
                if (matrix[mx, my])
                {
                    (minX, minY, maxX, maxY) = (Math.Min(minX, mx), Math.Min(minY, my), Math.Max(maxX, mx), Math.Max(maxY, my));
                }

                var dark = matrix[mx, my] ^ invert;
                var value = dark ? (byte)0x10 : (byte)0xFF;
                var p = ((y + my) * image.Stride) + ((x + mx) * 4);
                image.Pixels[p] = image.Pixels[p + 1] = image.Pixels[p + 2] = value;
                image.Pixels[p + 3] = 0xFF;
            }
        }

        return new PixelRect(x + minX, y + minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>Loads a file from Samples/ as premultiplied BGRA, like the app's Windows decoder does.</summary>
    public static BgraImage Sample(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Samples", name);
        var decoded = ImageResult.FromMemory(File.ReadAllBytes(path), ColorComponents.RedGreenBlueAlpha);
        var pixels = decoded.Data;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = pixels[i + 3];
            var (r, g, b) = (pixels[i], pixels[i + 1], pixels[i + 2]);
            pixels[i] = (byte)(b * a / 255);
            pixels[i + 1] = (byte)(g * a / 255);
            pixels[i + 2] = (byte)(r * a / 255);
        }

        return new BgraImage(pixels, decoded.Width, decoded.Height, decoded.Width * 4);
    }

    /// <summary>Asserts that an outline matches a rectangle's corners within a few pixels.</summary>
    public static void AssertOutline(PixelRect expected, IReadOnlyList<PixelPoint> corners, double tolerance = 2.5)
    {
        Assert.Equal(4, corners.Count);
        PixelPoint[] want =
        [
            new(expected.X, expected.Y),
            new(expected.X + expected.Width, expected.Y),
            new(expected.X + expected.Width, expected.Y + expected.Height),
            new(expected.X, expected.Y + expected.Height),
        ];

        foreach (var w in want)
        {
            var nearest = corners.Min(c => Math.Max(Math.Abs(c.X - w.X), Math.Abs(c.Y - w.Y)));
            Assert.True(nearest <= tolerance, $"No corner near ({w.X}, {w.Y}); got {string.Join(", ", corners.Select(c => $"({c.X:0.#}, {c.Y:0.#})"))}");
        }
    }
}
