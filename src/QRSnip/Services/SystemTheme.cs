using Microsoft.UI.Xaml;
using Microsoft.Win32;

namespace QRSnip.Services;

/// <summary>Reads the Windows colour mode settings that WinUI doesn't expose.</summary>
internal static class SystemTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>
    /// The taskbar, Start and the notification area follow the <em>Windows</em> mode, which can
    /// differ from the app mode (e.g. dark taskbar with light apps).
    /// </summary>
    public static bool TaskbarUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static ElementTheme TaskbarTheme => TaskbarUsesLightTheme() ? ElementTheme.Light : ElementTheme.Dark;
}
