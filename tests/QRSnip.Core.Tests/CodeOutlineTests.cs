using QRSnip.Core;

namespace QRSnip.Core.Tests;

public class CodeOutlineTests
{
    [Fact]
    public void Convex_hull_drops_inner_points()
    {
        PixelPoint[] points = [new(0, 0), new(10, 0), new(5, 5), new(10, 10), new(0, 10), new(5, 0)];

        var hull = CodeOutline.ConvexHull(points);

        Assert.Equal(4, hull.Length);
        Assert.DoesNotContain(new PixelPoint(5, 5), hull);
        Assert.DoesNotContain(new PixelPoint(5, 0), hull);
    }

    [Fact]
    public void Qr_corners_fall_back_to_an_estimate_without_contrast()
    {
        // A blank image: the finder edges can't be measured, but an outline is still returned.
        var luma = new byte[200 * 200];

        var corners = CodeOutline.QrCorners(new(50, 150), new(50, 50), new(150, 50), luma, 200, 200);

        Assert.Equal(4, corners.Length);
        Assert.True(corners[0].X < 50 && corners[0].Y < 50);
        Assert.True(corners[2].X > 150 && corners[2].Y > 150);
    }
}
