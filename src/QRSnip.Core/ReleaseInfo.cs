using System.Text;
using System.Text.RegularExpressions;

namespace QRSnip.Core;

/// <summary>Helpers for update checks: release tags and release notes.</summary>
public static partial class ReleaseInfo
{
    /// <summary>Parses a release tag such as "v1.2.0" or "1.2" (pre-release suffixes are ignored).</summary>
    public static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var match = VersionPattern().Match(tag.Trim());
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var parsed))
        {
            return false;
        }

        // Normalise to three parts so 1.2 == 1.2.0.
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    /// <summary>True when <paramref name="tag"/> names a newer version than <paramref name="current"/>.</summary>
    public static bool IsNewer(string? tag, Version current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return TryParseVersion(tag, out var version)
            && version > new Version(current.Major, current.Minor, Math.Max(current.Build, 0));
    }

    /// <summary>
    /// Turns a Markdown changelog section into short plain text for the update banner and
    /// notifications: headings and emphasis are dropped, list items become bullets.
    /// </summary>
    public static string ToPlainText(string? markdown, int maxLength = 600)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var lines = new List<string>();
        foreach (var raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("<!--", StringComparison.Ordinal) || LinkDefinition().IsMatch(line))
            {
                continue;
            }

            line = Heading().Replace(line, string.Empty);
            line = ListItem().Replace(line, "• ");
            line = Link().Replace(line, "$1");
            line = line.Replace("**", string.Empty, StringComparison.Ordinal)
                .Replace("__", string.Empty, StringComparison.Ordinal)
                .Replace("`", string.Empty, StringComparison.Ordinal);
            if (line.Length > 0 || (lines.Count > 0 && lines[^1].Length > 0))
            {
                lines.Add(line);
            }
        }

        var text = string.Join('\n', lines).Trim();
        if (text.Length > maxLength)
        {
            var cut = text.LastIndexOf('\n', maxLength);
            text = (cut > maxLength / 2 ? text[..cut] : text[..maxLength]).TrimEnd() + "\n…";
        }

        return text;
    }

    [GeneratedRegex(@"^v?(\d+(?:\.\d+){1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^#{1,6}\s+")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^[-*+]\s+")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"^\[[^\]]+\]:\s")]
    private static partial Regex LinkDefinition();
}
