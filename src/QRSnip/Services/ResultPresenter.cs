using QRSnip.Core;
using Windows.ApplicationModel.DataTransfer;

namespace QRSnip.Services;

/// <summary>Display strings and the actions a decoded payload supports.</summary>
public static class ResultPresenter
{
    public static string Glyph(ContentKind kind) => kind switch
    {
        ContentKind.Url => "",
        ContentKind.Email => "",
        ContentKind.Phone => "",
        ContentKind.Sms => "",
        ContentKind.Wifi => "",
        ContentKind.Contact => "",
        ContentKind.Geo => "",
        _ => "",
    };

    public static string Label(ContentKind kind) => kind switch
    {
        ContentKind.Url => "Link",
        ContentKind.Email => "Email",
        ContentKind.Phone => "Phone number",
        ContentKind.Sms => "SMS",
        ContentKind.Wifi => "Wi-Fi network",
        ContentKind.Contact => "Contact",
        ContentKind.Geo => "Location",
        _ => "Text",
    };

    public static string OpenLabel(ContentKind kind) => kind switch
    {
        ContentKind.Url => "Open link",
        ContentKind.Email => "Write email",
        ContentKind.Phone => "Call",
        ContentKind.Geo => "Open map",
        _ => "Open",
    };

    public static void Copy(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        try
        {
            // Keep the text available after QRSnip exits.
            Clipboard.Flush();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
    }

    /// <summary>Launches http(s), mailto, tel and geo URIs only; anything else is never executed.</summary>
    public static async Task<bool> OpenAsync(ContentInfo content)
    {
        if (content.LaunchUri is null || !Uri.TryCreate(content.LaunchUri, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme is not ("http" or "https" or "mailto" or "tel" or "geo"))
        {
            return false;
        }

        return await Windows.System.Launcher.LaunchUriAsync(uri);
    }
}
