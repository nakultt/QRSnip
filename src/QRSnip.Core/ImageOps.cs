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
