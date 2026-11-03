using QRSnip.Core;

namespace QRSnip.Core.Tests;

public class ReleaseInfoTests
{
    [Theory]
    [InlineData("v1.1.0", "1.0.0", true)]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("v1.0", "1.0.0", false)]
    [InlineData("v1.0.0", "1.0.0.0", false)]
    [InlineData("v0.9.9", "1.0.0", false)]
    [InlineData("v2.0.0-beta.1", "1.9.0", true)]
    [InlineData("nightly", "1.0.0", false)]
    [InlineData(null, "1.0.0", false)]
    public void Compares_release_tags_with_the_running_version(string? tag, string current, bool newer)
    {
        Assert.Equal(newer, ReleaseInfo.IsNewer(tag, Version.Parse(current)));
    }

    [Fact]
    public void Flattens_changelog_markdown()
    {
        const string Notes = """
            ### Added

            - **Full screen** mode with `Shift+F10`.
            - Scan [image files](https://example.com/docs).

            [1.2.0]: https://github.com/nakultt/QRSnip/releases/tag/v1.2.0
            """;

        Assert.Equal("Added\n\n• Full screen mode with Shift+F10.\n• Scan image files.", ReleaseInfo.ToPlainText(Notes));
    }

    [Fact]
    public void Shortens_long_notes_at_a_line_break()
    {
        var notes = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"- Change number {i}"));

        var text = ReleaseInfo.ToPlainText(notes, maxLength: 100);

        Assert.True(text.Length <= 102, text);
        Assert.EndsWith("\n…", text);
        Assert.StartsWith("• Change number 1\n", text);
    }
}
