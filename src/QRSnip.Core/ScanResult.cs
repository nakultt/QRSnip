namespace QRSnip.Core;

/// <summary>What a decoded payload represents, used to pick the right actions in the UI.</summary>
public enum ContentKind
{
    Text,
    Url,
    Email,
    Phone,
    Sms,
    Wifi,
    Contact,
    Geo,
}

/// <summary>A point in image pixels. Pixel <c>i</c> covers <c>[i, i + 1)</c>, so edges fall on whole numbers.</summary>
public readonly record struct PixelPoint(double X, double Y);

/// <summary>A single decoded code.</summary>
/// <param name="Text">The raw decoded payload.</param>
/// <param name="Format">The symbology, e.g. "QR_CODE".</param>
public sealed record ScanResult(string Text, string Format)
{
    public ContentInfo Content { get; } = ContentClassifier.Classify(Text);

    /// <summary>
    /// Outline of the code in the scanned image's pixels, in order around the code (for QR codes:
    /// its top-left, top-right, bottom-right and bottom-left corners). Empty when unknown.
    /// </summary>
    public IReadOnlyList<PixelPoint> Corners { get; init; } = [];

    /// <summary>The axis-aligned box around <see cref="Corners"/>, or an empty rect when unknown.</summary>
    public PixelRect Bounds
    {
        get
        {
            if (Corners.Count == 0)
            {
                return default;
            }

            var left = (int)Math.Floor(Corners.Min(p => p.X));
            var top = (int)Math.Floor(Corners.Min(p => p.Y));
            var right = (int)Math.Ceiling(Corners.Max(p => p.X));
            var bottom = (int)Math.Ceiling(Corners.Max(p => p.Y));
            return new PixelRect(left, top, right - left, bottom - top);
        }
    }
}

/// <summary>A classified payload with a short display title and, where relevant, extra fields.</summary>
public sealed record ContentInfo(ContentKind Kind, string Title, string? LaunchUri = null, string? Secret = null);
