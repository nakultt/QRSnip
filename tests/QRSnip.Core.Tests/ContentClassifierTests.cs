using QRSnip.Core;

namespace QRSnip.Core.Tests;

public class ContentClassifierTests
{
    [Theory]
    [InlineData("https://github.com/nakultt/QRSnip", "github.com", "https://github.com/nakultt/QRSnip")]
    [InlineData("http://example.com", "example.com", "http://example.com/")]
    [InlineData("www.example.org/path?q=1", "www.example.org", "https://www.example.org/path?q=1")]
    [InlineData("  https://example.com  ", "example.com", "https://example.com/")]
    public void Recognises_web_urls(string input, string title, string launch)
    {
        var info = ContentClassifier.Classify(input);

        Assert.Equal(ContentKind.Url, info.Kind);
        Assert.Equal(title, info.Title);
        Assert.Equal(launch, info.LaunchUri);
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("C:\\Windows")]
    [InlineData("version 1.2")]
    [InlineData("user@example.com")]
    public void Does_not_treat_other_text_as_a_link(string input)
    {
        Assert.NotEqual(ContentKind.Url, ContentClassifier.Classify(input).Kind);
    }

    [Fact]
    public void Parses_wifi_with_escapes()
    {
        var info = ContentClassifier.Classify(@"WIFI:T:WPA;S:Cafe\;Guest;P:p\:ss\\word;;");

        Assert.Equal(ContentKind.Wifi, info.Kind);
        Assert.Equal("Cafe;Guest", info.Title);
        Assert.Equal(@"p:ss\word", info.Secret);
    }

    [Fact]
    public void Parses_open_wifi_without_password()
    {
        var info = ContentClassifier.Classify("WIFI:S:Airport;T:nopass;;");

        Assert.Equal(ContentKind.Wifi, info.Kind);
        Assert.Equal("Airport", info.Title);
        Assert.Null(info.Secret);
    }

    [Fact]
    public void Parses_mailto_and_tel()
    {
        var mail = ContentClassifier.Classify("mailto:hi@example.com?subject=Hey");
        var tel = ContentClassifier.Classify("tel:+911234567890");

        Assert.Equal(ContentKind.Email, mail.Kind);
        Assert.Equal("hi@example.com", mail.Title);
        Assert.Equal(ContentKind.Phone, tel.Kind);
        Assert.Equal("+911234567890", tel.Title);
    }

    [Fact]
    public void Reads_vcard_name()
    {
        var info = ContentClassifier.Classify("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Ada Lovelace\r\nEND:VCARD");

        Assert.Equal(ContentKind.Contact, info.Kind);
        Assert.Equal("Ada Lovelace", info.Title);
    }

    [Fact]
    public void Uses_first_line_as_text_title()
    {
        var info = ContentClassifier.Classify("first line\nsecond line");

        Assert.Equal(ContentKind.Text, info.Kind);
        Assert.Equal("first line", info.Title);
    }
}
