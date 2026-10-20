using System.Text;

namespace QRSnip.Core;

/// <summary>Recognises common QR payload conventions (URLs, Wi-Fi, mailto, tel, vCard...).</summary>
public static class ContentClassifier
{
    public static ContentInfo Classify(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();

        if (TryParseWifi(trimmed, out var ssid, out var password))
        {
            return new ContentInfo(ContentKind.Wifi, ssid.Length > 0 ? ssid : "Wi-Fi network", Secret: password);
        }

        if (trimmed.StartsWith("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("MECARD:", StringComparison.OrdinalIgnoreCase))
        {
            return new ContentInfo(ContentKind.Contact, ContactName(trimmed) ?? "Contact card");
        }

        if (trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return new ContentInfo(ContentKind.Email, trimmed[7..].Split('?')[0], trimmed);
        }

        if (trimmed.StartsWith("MATMSG:", StringComparison.OrdinalIgnoreCase))
        {
            var to = Field(trimmed[7..], "TO") ?? "Email";
            return new ContentInfo(ContentKind.Email, to, $"mailto:{to}");
        }

        if (trimmed.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
        {
            return new ContentInfo(ContentKind.Phone, trimmed[4..], trimmed);
        }

        if (trimmed.StartsWith("smsto:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("sms:", StringComparison.OrdinalIgnoreCase))
        {
            var body = trimmed[(trimmed.IndexOf(':', StringComparison.Ordinal) + 1)..];
            return new ContentInfo(ContentKind.Sms, body.Split(':')[0]);
        }

        if (trimmed.StartsWith("geo:", StringComparison.OrdinalIgnoreCase))
        {
            return new ContentInfo(ContentKind.Geo, trimmed[4..].Split('?')[0], trimmed);
        }

        if (TryParseWebUrl(trimmed, out var uri))
        {
            return new ContentInfo(ContentKind.Url, uri.IdnHost, uri.AbsoluteUri);
        }

        var firstLine = trimmed.Split('\n', 2)[0].Trim();
        return new ContentInfo(ContentKind.Text, firstLine.Length > 0 ? firstLine : "Text");
    }

    /// <summary>Accepts http(s) URLs, and bare domains such as "example.com/path".</summary>
    public static bool TryParseWebUrl(string text, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute))
        {
            if (absolute.Scheme is "http" or "https")
            {
                uri = absolute;
                return true;
            }

            // "example.com:8080" parses as scheme "example.com"; only real schemes stop here.
            if (!absolute.Scheme.Contains('.', StringComparison.Ordinal))
            {
                return false;
            }
        }

        // Bare host like "www.example.com/abc": require a dot and a plausible TLD.
        var host = text.Split('/', '?', '#')[0].Split(':')[0];
        var lastDot = host.LastIndexOf('.');
        if (lastDot <= 0 || lastDot == host.Length - 1 || host.Contains('@', StringComparison.Ordinal))
        {
            return false;
        }

        var tld = host[(lastDot + 1)..];
        if (tld.Length < 2 || !tld.All(char.IsLetter))
        {
            return false;
        }

        return Uri.TryCreate("https://" + text, UriKind.Absolute, out uri!);
    }

    /// <summary>Parses the de-facto "WIFI:T:WPA;S:name;P:secret;;" format, honouring backslash escapes.</summary>
    public static bool TryParseWifi(string text, out string ssid, out string? password)
    {
        ssid = string.Empty;
        password = null;
        if (!text.StartsWith("WIFI:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var (key, value) in SplitFields(text[5..]))
        {
            switch (key.ToUpperInvariant())
            {
                case "S":
                    ssid = value;
                    break;
                case "P":
                    password = value;
                    break;
            }
        }

        return true;
    }

    private static string? ContactName(string text)
    {
        if (text.StartsWith("MECARD:", StringComparison.OrdinalIgnoreCase))
        {
            return Field(text[7..], "N")?.Replace(',', ' ');
        }

        foreach (var line in text.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.StartsWith("FN:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("FN;", StringComparison.OrdinalIgnoreCase))
            {
                return l[(l.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
            }
        }

        return null;
    }

    private static string? Field(string body, string name)
    {
        foreach (var (key, value) in SplitFields(body))
        {
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static IEnumerable<(string Key, string Value)> SplitFields(string body)
    {
        var current = new StringBuilder();
        var parts = new List<string>();
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '\\' && i + 1 < body.Length)
            {
                current.Append(body[++i]);
            }
            else if (c == ';')
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        foreach (var part in parts)
        {
            var colon = part.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                yield return (part[..colon], part[(colon + 1)..]);
            }
        }
    }
}
