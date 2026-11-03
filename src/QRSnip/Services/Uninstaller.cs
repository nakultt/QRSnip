namespace QRSnip.Services;

/// <summary>Clean-up when the GitHub installer (Velopack) removes QRSnip.</summary>
internal static class Uninstaller
{
    private static readonly string[] DataFiles = ["settings.json", "history.json", "crash.log", "settings.json.tmp", "history.json.tmp"];

    /// <summary>
    /// Runs inside Velopack's uninstall hook (30 s limit). Velopack deletes the program folder;
    /// this removes what QRSnip created elsewhere: the Start with Windows entry, the notification
    /// registration, and settings, history and crash log.
    /// </summary>
    public static void CleanUp()
    {
        Try(StartupService.RemoveRunEntryForThisCopy);
        Try(NotificationService.UnregisterAll);
        Try(() =>
        {
            foreach (var name in DataFiles)
            {
                File.Delete(Path.Combine(AppStorage.DataFolder, name));
            }

            if (Directory.Exists(AppStorage.DataFolder) && !Directory.EnumerateFileSystemEntries(AppStorage.DataFolder).Any())
            {
                Directory.Delete(AppStorage.DataFolder);
            }
        });

        static void Try(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }
    }
}
