using QRSnip.Core;
using ZXing;

namespace QRSnip.Core.Tests;

public class QrScannerTests
{
    [Fact]
    public void Decodes_a_code_inside_the_selected_region()
    {
        var image = Canvas(800, 600);
        Stamp(image, "https://github.com/nakultt/QRSnip", x: 300, y: 150, size: 240);

        var results = QrScanner.Scan(image, new PixelRect(280, 130, 280, 280));

        var result = Assert.Single(results);
        Assert.Equal("https://github.com/nakultt/QRSnip", result.Text);
        Assert.Equal("QR_CODE", result.Format);
        Assert.Equal(ContentKind.Url, result.Content.Kind);
    }

    [Fact]
    public void Ignores_codes_outside_the_region()
    {
        var image = Canvas(800, 600);
        Stamp(image, "outside", x: 500, y: 300, size: 200);

        Assert.Empty(QrScanner.Scan(image, new PixelRect(0, 0, 300, 300)));
    }

    [Fact]
    public void Finds_several_codes_in_one_region()
    {
        var image = Canvas(900, 400);
        Stamp(image, "first", x: 40, y: 60, size: 260);
        Stamp(image, "second", x: 520, y: 60, size: 260);

        var texts = QrScanner.Scan(image).Select(r => r.Text).Order().ToArray();

        Assert.Equal(["first", "second"], texts);
    }

    [Fact]
    public void Decodes_tiny_codes_by_upscaling()
    {
        var image = Canvas(200, 200);
        // ~1.5 px per module, smaller than ZXing reliably reads at native size.
        Stamp(image, "tiny", x: 60, y: 60, size: 40);

        var result = Assert.Single(QrScanner.Scan(image, new PixelRect(50, 50, 60, 60)));
        Assert.Equal("tiny", result.Text);
    }

    [Fact]
    public void Decodes_light_on_dark_codes()
    {
        var image = Canvas(400, 400);
        Stamp(image, "inverted", x: 80, y: 80, size: 240, invert: true);

        var result = Assert.Single(QrScanner.Scan(image));
        Assert.Equal("inverted", result.Text);
    }

    [Fact]
    public void Clamps_regions_that_leave_the_image()
    {
        var image = Canvas(100, 100);

        Assert.Empty(QrScanner.Scan(image, new PixelRect(-50, -50, 400, 400)));
        Assert.Empty(QrScanner.Scan(image, new PixelRect(500, 500, 10, 10)));
    }

    private static BgraImage Canvas(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        Array.Fill(pixels, (byte)0xF0);
        return new BgraImage(pixels, width, height, stride);
    }

    private static void Stamp(BgraImage image, string text, int x, int y, int size, bool invert = false)
    {
        var matrix = new MultiFormatWriter().encode(
            text,
            BarcodeFormat.QR_CODE,
            size,
            size,
            new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 1 });

        for (var my = 0; my < matrix.Height; my++)
        {
            for (var mx = 0; mx < matrix.Width; mx++)
            {
                var dark = matrix[mx, my] ^ invert;
                var value = dark ? (byte)0x10 : (byte)0xFF;
                var p = ((y + my) * image.Stride) + ((x + mx) * 4);
                image.Pixels[p] = image.Pixels[p + 1] = image.Pixels[p + 2] = value;
                image.Pixels[p + 3] = 0xFF;
            }
        }
    }
}
