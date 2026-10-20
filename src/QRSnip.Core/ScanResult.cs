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

/// <summary>A single decoded code.</summary>
/// <param name="Text">The raw decoded payload.</param>
/// <param name="Format">The symbology, e.g. "QR_CODE".</param>
public sealed record ScanResult(string Text, string Format)
{
    public ContentInfo Content { get; } = ContentClassifier.Classify(Text);
}

/// <summary>A classified payload with a short display title and, where relevant, extra fields.</summary>
public sealed record ContentInfo(ContentKind Kind, string Title, string? LaunchUri = null, string? Secret = null);
