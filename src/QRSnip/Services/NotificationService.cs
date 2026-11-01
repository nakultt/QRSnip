using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace QRSnip.Services;

/// <summary>
/// Windows notifications through the Windows App SDK, falling back to a notification-area
/// balloon (which Windows also shows as a toast) if app notifications aren't available.
/// </summary>
internal sealed class NotificationService
{
    public const string ActionArgument = "action";
    public const string OpenAction = "open";

    private readonly DispatcherQueue _dispatcher;
    private readonly Func<ShellHost?> _shell;
    private bool _registered;

    public NotificationService(DispatcherQueue dispatcher, Func<ShellHost?> shell)
    {
        _dispatcher = dispatcher;
        _shell = shell;
    }

    /// <summary>A notification (or one of its buttons) was clicked; the argument is its action.</summary>
    public event EventHandler<string>? Invoked;

    /// <summary>Call once at startup, before any notification is shown.</summary>
    public void Register()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            // Not supported on this system or blocked by policy: balloons still work.
            System.Diagnostics.Debug.WriteLine(ex);
            _registered = false;
        }
    }

    /// <summary>Removes QRSnip's notification registration (used when uninstalling).</summary>
    public static void UnregisterAll()
    {
        try
        {
            AppNotificationManager.Default.UnregisterAll();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    /// <summary>Reads the action from a notification that launched or activated QRSnip.</summary>
    public static string ActionOf(AppNotificationActivatedEventArgs args) =>
        args.Arguments.TryGetValue(ActionArgument, out var action) ? action : OpenAction;

    public void Show(string title, string body, string? buttonText = null, string? buttonAction = null)
    {
        if (_registered)
        {
            try
            {
                var builder = new AppNotificationBuilder()
                    .AddArgument(ActionArgument, OpenAction)
                    .AddText(title)
                    .AddText(body);
                if (buttonText is not null && buttonAction is not null)
                {
                    builder.AddButton(new AppNotificationButton(buttonText).AddArgument(ActionArgument, buttonAction));
                }

                AppNotificationManager.Default.Show(builder.BuildNotification());
                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        _shell()?.ShowBalloon(title, body);
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        // Raised on a background thread.
        var action = ActionOf(args);
        _dispatcher.TryEnqueue(() => Invoked?.Invoke(this, action));
    }
}
