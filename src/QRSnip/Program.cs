using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using QRSnip.Native;

namespace QRSnip;

/// <summary>Custom entry point so a second launch is forwarded to the running instance.</summary>
public static class Program
{
    private const string InstanceKey = "QRSnip.Main";

    [STAThread]
    public static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!main.IsCurrent)
        {
            RedirectAndWait(main, activation);
            return 0;
        }

        Application.Start(_ignored =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(main, activation);
        });

        return 0;
    }

    private static unsafe void RedirectAndWait(AppInstance target, AppActivationArguments args)
    {
        // Let the running instance bring its window to the front.
        Win32.AllowSetForegroundWindow(target.ProcessId);

        // Redirecting on the STA would deadlock, so do it on a worker and pump COM while waiting.
        var done = Win32.CreateEvent(0, true, false, 0);
        var signal = done;
        _ = Task.Run(() =>
        {
            target.RedirectActivationToAsync(args).AsTask().Wait();
            Win32.SetEvent(signal);
        });

        const uint Infinite = 0xFFFFFFFF;
        Win32.CoWaitForMultipleObjects(0, Infinite, 1, &done, out _);
    }
}
