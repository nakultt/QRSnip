namespace QRSnip.Core;

/// <summary>Pixel helpers for images that don't come from the screen.</summary>
public static class ImageOps
{
    /// <summary>
    /// Composites a premultiplied-alpha image onto white, in place, and returns it. A QR code
    /// drawn in black on a transparent PNG would otherwise read as black on black.
    /// </summary>
    public static BgraImage FlattenOnWhite(BgraImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var px = image.Pixels;
        for (var y = 0; y < image.Height; y++)
        {
            var row = y * image.Stride;
            for (var x = 0; x < image.Width; x++)
            {
                var p = row + (x * 4);
                var cover = 255 - px[p + 3];
                if (cover == 0)
                {
                    continue;
                }

                px[p] = (byte)Math.Min(255, px[p] + cover);
                px[p + 1] = (byte)Math.Min(255, px[p + 1] + cover);
                px[p + 2] = (byte)Math.Min(255, px[p + 2] + cover);
                px[p + 3] = 255;
            }
        }

        return image;
    }

    /// <summary>
    /// Returns a copy that fits in <paramref name="maxSide"/> pixels (area averaging), or the
    /// image itself if it already fits. Used for previews of large photos.
    /// </summary>
    public static BgraImage Shrink(BgraImage image, int maxSide)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSide, 1);
        var longest = Math.Max(image.Width, image.Height);
        if (longest <= maxSide)
        {
            return image;
        }

        var w = Math.Max(1, (int)((long)image.Width * maxSide / longest));
        var h = Math.Max(1, (int)((long)image.Height * maxSide / longest));
        var output = new byte[w * h * 4];
        var src = image.Pixels;
        for (var y = 0; y < h; y++)
        {
            var y0 = (int)((long)y * image.Height / h);
            var y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * image.Height / h));
            for (var x = 0; x < w; x++)
            {
                var x0 = (int)((long)x * image.Width / w);
                var x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * image.Width / w));
                int b = 0, g = 0, r = 0, a = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var row = sy * image.Stride;
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var p = row + (sx * 4);
                        b += src[p];
                        g += src[p + 1];
                        r += src[p + 2];
                        a += src[p + 3];
                    }
                }

                var n = (y1 - y0) * (x1 - x0);
                var d = ((y * w) + x) * 4;
                output[d] = (byte)(b / n);
                output[d + 1] = (byte)(g / n);
                output[d + 2] = (byte)(r / n);
                output[d + 3] = (byte)(a / n);
            }
        }

        return new BgraImage(output, w, h, w * 4);
    }
}

/// <summary>
/// Something that produces pictures to scan: an image file, the clipboard, or a camera
/// (each call returns the newest frame). The app implements these with Windows imaging APIs.
/// </summary>
public interface IImageSource
{
    /// <summary>A short name for messages, e.g. the file name.</summary>
    string Name { get; }

    Task<BgraImage> GetImageAsync(CancellationToken cancellationToken = default);
}
