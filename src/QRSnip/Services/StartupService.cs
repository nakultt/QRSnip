using Microsoft.Win32;
using Windows.ApplicationModel;

namespace QRSnip.Services;

/// <summary>"Start with Windows": a StartupTask when packaged, the HKCU Run key otherwise.</summary>
internal static class StartupService
{
    private const string TaskId = "QRSnipStartup";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "QRSnip";

    public static bool IsPackaged { get; } = DetectPackage();

    public static async Task<bool> IsEnabledAsync()
    {
        if (IsPackaged)
        {
            var task = await StartupTask.GetAsync(TaskId);
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    /// <summary>Returns the resulting state, which may differ if the user or policy disabled it.</summary>
    public static async Task<bool> SetEnabledAsync(bool enabled)
    {
        if (IsPackaged)
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (enabled)
            {
                var state = await task.RequestEnableAsync();
                return state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            }

            task.Disable();
            return false;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --background");
        }
        else
        {
            key.DeleteValue(RunValue, throwOnMissingValue: false);
        }

        return enabled;
    }

    private static bool DetectPackage()
    {
        try
        {
            return Package.Current is not null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
