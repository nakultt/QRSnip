using QRSnip.Core;

using static QRSnip.Core.Tests.TestImages;

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

    [Fact]
    public void Reports_the_outline_of_a_code_in_whole_image_pixels()
    {
        var image = Canvas(800, 600);
        var code = Stamp(image, "https://github.com/nakultt/QRSnip", x: 300, y: 150, size: 240);

        var result = Assert.Single(QrScanner.Scan(image, new PixelRect(280, 130, 280, 280)));

        AssertOutline(code, result.Corners);
        Assert.Equal(code.X, result.Bounds.X, tolerance: 3);
        Assert.Equal(code.Width, result.Bounds.Width, tolerance: 5);
    }

    [Fact]
    public void Maps_the_outline_of_an_upscaled_tiny_code_back_to_the_screen()
    {
        var image = Canvas(200, 200);
        var code = Stamp(image, "tiny", x: 60, y: 60, size: 40);

        var result = Assert.Single(QrScanner.Scan(image, new PixelRect(50, 50, 60, 60)));

        AssertOutline(code, result.Corners, tolerance: 2);
    }

    [Fact]
    public void Reports_an_outline_for_each_of_several_codes()
    {
        var image = Canvas(900, 400);
        var first = Stamp(image, "first", x: 40, y: 60, size: 260);
        var second = Stamp(image, "second", x: 520, y: 60, size: 220);

        var results = QrScanner.Scan(image).ToDictionary(r => r.Text);

        AssertOutline(first, results["first"].Corners);
        AssertOutline(second, results["second"].Corners);
    }

    [Fact]
    public void Outlines_inverted_codes()
    {
        var image = Canvas(400, 400);
        var code = Stamp(image, "inverted", x: 80, y: 80, size: 240, invert: true);

        AssertOutline(code, Assert.Single(QrScanner.Scan(image)).Corners);
    }

    [Fact]
    public void Scans_regions_in_parallel_and_keeps_whole_image_positions()
    {
        // Two "monitors" side by side, each with a code, plus one code shown on both.
        var image = Canvas(1600, 500);
        var left = Stamp(image, "left monitor", x: 100, y: 100, size: 250);
        var right = Stamp(image, "right monitor", x: 1200, y: 150, size: 250);
        Stamp(image, "same", x: 450, y: 100, size: 200);
        Stamp(image, "same", x: 850, y: 100, size: 200);

        var results = QrScanner.Scan(image, [new PixelRect(0, 0, 800, 500), new PixelRect(800, 0, 800, 500)]);

        Assert.Equal(["left monitor", "right monitor", "same"], results.Select(r => r.Text).Order().ToArray());
        AssertOutline(left, results.Single(r => r.Text == "left monitor").Corners);
        AssertOutline(right, results.Single(r => r.Text == "right monitor").Corners);
    }

    [Fact]
    public void Bounds_are_empty_without_corners()
    {
        Assert.True(new ScanResult("x", "QR_CODE").Bounds.IsEmpty);
    }
}
