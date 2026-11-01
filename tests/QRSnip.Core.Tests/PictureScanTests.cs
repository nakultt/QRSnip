using System.Diagnostics;
using QRSnip.Core;

using static QRSnip.Core.Tests.TestImages;

namespace QRSnip.Core.Tests;

/// <summary>Pictures opened from files, dropped or pasted (issue #5). Samples are made by Samples/generate.py.</summary>
public class PictureScanTests
{
    [Theory]
    [InlineData("rotated.png", "https://example.com/rotated")]
    [InlineData("small.png", "small")]
    [InlineData("photo.jpg", "https://example.com/photo")]
    public void Decodes_sample_pictures(string file, string expected)
    {
        var result = Assert.Single(QrScanner.ScanPicture(Sample(file), TestContext.Current.CancellationToken));

        Assert.Equal(expected, result.Text);
        Assert.Equal(4, result.Corners.Count);
    }

    [Fact]
    public void Finds_every_code_in_a_picture()
    {
        var texts = QrScanner.ScanPicture(Sample("multiple.png"), TestContext.Current.CancellationToken).Select(r => r.Text).Order().ToArray();

        Assert.Equal(["https://example.com/three", "one", "two"], texts);
    }

    [Fact]
    public void Rotated_code_outline_surrounds_its_centre()
    {
        var result = Assert.Single(QrScanner.ScanPicture(Sample("rotated.png"), TestContext.Current.CancellationToken));

        // The code is centred in the 800x600 picture and turned by 30 degrees.
        var cx = result.Corners.Average(c => c.X);
        var cy = result.Corners.Average(c => c.Y);
        Assert.InRange(cx, 390, 410);
        Assert.InRange(cy, 290, 310);
        Assert.True(result.Corners.Select(c => c.Y).Distinct().Count() == 4, "A turned code has no horizontal edges.");
    }

    [Fact]
    public void Reads_codes_on_a_transparent_background_after_flattening()
    {
        var image = ImageOps.FlattenOnWhite(Sample("transparent.png"));

        Assert.Equal("transparent", Assert.Single(QrScanner.ScanPicture(image, TestContext.Current.CancellationToken)).Text);
    }

    [Fact]
    public void Flattening_composites_translucent_pixels_onto_white()
    {
        // Premultiplied: transparent, half-transparent black, opaque red (BGRA order).
        var image = new BgraImage([0, 0, 0, 0, 0, 0, 0, 128, 0, 0, 255, 255], 3, 1, 12);

        ImageOps.FlattenOnWhite(image);

        Assert.Equal([255, 255, 255, 255, 127, 127, 127, 255, 0, 0, 255, 255], image.Pixels);
    }

    [Fact]
    public void Decodes_a_12_megapixel_photo_quickly()
    {
        // 4000 x 3000, code about a fifth of the width, like a phone photo of a printout.
        var image = Canvas(4000, 3000, shade: 0xC8);
        var code = Stamp(image, "https://example.com/12mp", x: 1700, y: 1200, size: 820);

        QrScanner.ScanPicture(Canvas(400, 300), TestContext.Current.CancellationToken); // warm up
        var timer = Stopwatch.StartNew();
        var result = Assert.Single(QrScanner.ScanPicture(image, TestContext.Current.CancellationToken));
        timer.Stop();

        Assert.Equal("https://example.com/12mp", result.Text);
        AssertOutline(code, result.Corners, tolerance: 4);
        // Generous bound for slow CI machines; typically well under a second.
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"Took {timer.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Reduces_large_pictures_when_full_size_finds_nothing()
    {
        // A heavily blurred code: unreadable at full size, fine once reduced.
        var image = Canvas(3200, 2400, shade: 0xD0);
        Stamp(image, "blurry", x: 1000, y: 700, size: 1000);
        BoxBlur(image, radius: 9);

        var result = Assert.Single(QrScanner.ScanPicture(image, TestContext.Current.CancellationToken));

        Assert.Equal("blurry", result.Text);
        Assert.InRange(result.Bounds.X, 980, 1060);
    }

    [Fact]
    public void Shrinks_large_pictures_for_previews_keeping_the_aspect_ratio()
    {
        var large = Canvas(4000, 3000, shade: 0x80);

        var preview = ImageOps.Shrink(large, 1600);

        Assert.Equal((1600, 1200), (preview.Width, preview.Height));
        Assert.All(preview.Pixels.Where((_, i) => i % 4 != 3), v => Assert.Equal(0x80, v));
        Assert.Same(preview, ImageOps.Shrink(preview, 1600));
    }

    [Fact]
    public void Downscale_averages_pixel_blocks()
    {
        byte[] luma =
        [
            0, 100, 200, 200,
            100, 0, 200, 200,
        ];

        var (pixels, width, height) = QrScanner.Downscale(luma, 4, 2, 0.5);

        Assert.Equal((2, 1), (width, height));
        Assert.Equal([50, 200], pixels);
    }

    private static void BoxBlur(BgraImage image, int radius)
    {
        var luma = QrScanner.ToLuminance(image, new PixelRect(0, 0, image.Width, image.Height));
        var tmp = new byte[luma.Length];
        Pass(luma, tmp, image.Width, image.Height, 1, image.Width);
        Pass(tmp, luma, image.Height, image.Width, image.Width, 1);
        for (var i = 0; i < luma.Length; i++)
        {
            image.Pixels[i * 4] = image.Pixels[(i * 4) + 1] = image.Pixels[(i * 4) + 2] = luma[i];
        }

        void Pass(byte[] src, byte[] dst, int length, int lines, int step, int lineStep)
        {
            for (var line = 0; line < lines; line++)
            {
                var start = line * lineStep;
                var sum = 0;
                for (var i = -radius; i <= radius; i++)
                {
                    sum += src[start + (Math.Clamp(i, 0, length - 1) * step)];
                }

                for (var i = 0; i < length; i++)
                {
                    dst[start + (i * step)] = (byte)(sum / ((2 * radius) + 1));
                    sum += src[start + (Math.Min(i + radius + 1, length - 1) * step)] - src[start + (Math.Max(i - radius, 0) * step)];
                }
            }
        }
    }
}
