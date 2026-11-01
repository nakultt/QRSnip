using ZXing;

namespace QRSnip.Core;

/// <summary>Turns the reference points ZXing reports for a code into the code's outline.</summary>
internal static class CodeOutline
{
    /// <summary>A QR finder pattern's centre is 3.5 modules in from the code's edges.</summary>
    private const double FinderCentreInset = 3.5;

    public static PixelPoint[] Find(Result result, byte[] luma, int width, int height)
    {
        var points = (result.ResultPoints ?? [])
            .Where(p => p is not null)
            .Select(p => new PixelPoint(p.X, p.Y))
            .ToArray();

        if (result.BarcodeFormat == BarcodeFormat.QR_CODE && points.Length >= 3)
        {
            return QrCorners(points[0], points[1], points[2], luma, width, height);
        }

        return points.Length >= 3 ? ConvexHull(points) : [];
    }

    /// <summary>
    /// ZXing reports the centres of the bottom-left, top-left and top-right finder patterns.
    /// The corners lie 3.5 modules further out, so the module size is measured on the finder
    /// patterns themselves (dark 1.5, light 1, dark 1 module from the centre outwards).
    /// </summary>
    internal static PixelPoint[] QrCorners(PixelPoint bottomLeft, PixelPoint topLeft, PixelPoint topRight, byte[] luma, int width, int height)
    {
        var across = Sub(topRight, topLeft);
        var down = Sub(bottomLeft, topLeft);
        var acrossLength = Length(across);
        var downLength = Length(down);
        if (acrossLength < 1 || downLength < 1)
        {
            return [];
        }

        var u = Scale(across, 1 / acrossLength);
        var v = Scale(down, 1 / downLength);
        var reach = Math.Min(acrossLength, downLength) / 2;

        var estimates = new[]
            {
                FinderEdge(luma, width, height, topLeft, Scale(u, -1), reach),
                FinderEdge(luma, width, height, topLeft, Scale(v, -1), reach),
                FinderEdge(luma, width, height, topRight, u, reach),
                FinderEdge(luma, width, height, topRight, Scale(v, -1), reach),
                FinderEdge(luma, width, height, bottomLeft, v, reach),
                FinderEdge(luma, width, height, bottomLeft, Scale(u, -1), reach),
            }
            .Where(d => d > 0)
            .Order()
            .ToArray();

        // Fall back to a version 2 code (25 modules, finder centres 18 modules apart) if the
        // edges couldn't be measured, e.g. a code touching the screen edge.
        var inset = estimates.Length > 0 ? estimates[estimates.Length / 2] : acrossLength / 18 * FinderCentreInset;

        var outU = Scale(u, inset);
        var outV = Scale(v, inset);
        var bottomRight = Add(topRight, down);
        return
        [
            Sub(Sub(topLeft, outU), outV),
            Sub(Add(topRight, outU), outV),
            Add(Add(bottomRight, outU), outV),
            Add(Sub(bottomLeft, outU), outV),
        ];
    }

    /// <summary>
    /// Walks from a finder centre in <paramref name="direction"/> and returns the distance to the
    /// pattern's outer edge (the third light/dark change), or 0 if it can't be found.
    /// </summary>
    private static double FinderEdge(byte[] luma, int width, int height, PixelPoint centre, PixelPoint direction, double reach)
    {
        const double Step = 0.5;
        var count = (int)(reach / Step);
        if (count < 8)
        {
            return 0;
        }

        var samples = new byte[count];
        var valid = 0;
        for (; valid < count; valid++)
        {
            var x = (int)Math.Floor(centre.X + (direction.X * valid * Step));
            var y = (int)Math.Floor(centre.Y + (direction.Y * valid * Step));
            if (x < 0 || y < 0 || x >= width || y >= height)
            {
                break;
            }

            samples[valid] = luma[(y * width) + x];
        }

        if (valid < 8)
        {
            return 0;
        }

        var span = samples.AsSpan(0, valid);
        int min = 255, max = 0;
        foreach (var s in span)
        {
            min = Math.Min(min, s);
            max = Math.Max(max, s);
        }

        if (max - min < 32)
        {
            return 0;
        }

        // Works for inverted (light-on-dark) codes too: only changes are counted.
        var threshold = (min + max) / 2;
        var dark = span[0] < threshold;
        var changes = 0;
        for (var i = 1; i < span.Length; i++)
        {
            var isDark = span[i] < threshold;
            if (isDark != dark)
            {
                dark = isDark;
                if (++changes == 3)
                {
                    // The edge lies between the last two samples.
                    return (i - 0.5) * Step;
                }
            }
        }

        return 0;
    }

    /// <summary>Andrew's monotone chain; returns the hull in order around the points.</summary>
    internal static PixelPoint[] ConvexHull(PixelPoint[] points)
    {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        if (sorted.Length < 3)
        {
            return sorted;
        }

        var hull = new PixelPoint[sorted.Length * 2];
        var k = 0;
        foreach (var p in sorted)
        {
            while (k >= 2 && Cross(hull[k - 2], hull[k - 1], p) <= 0)
            {
                k--;
            }

            hull[k++] = p;
        }

        for (int i = sorted.Length - 2, lower = k + 1; i >= 0; i--)
        {
            var p = sorted[i];
            while (k >= lower && Cross(hull[k - 2], hull[k - 1], p) <= 0)
            {
                k--;
            }

            hull[k++] = p;
        }

        return hull[..(k - 1)];
    }

    private static double Cross(PixelPoint o, PixelPoint a, PixelPoint b) =>
        ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));

    private static PixelPoint Add(PixelPoint a, PixelPoint b) => new(a.X + b.X, a.Y + b.Y);

    private static PixelPoint Sub(PixelPoint a, PixelPoint b) => new(a.X - b.X, a.Y - b.Y);

    private static PixelPoint Scale(PixelPoint a, double s) => new(a.X * s, a.Y * s);

    private static double Length(PixelPoint a) => Math.Sqrt((a.X * a.X) + (a.Y * a.Y));
}
