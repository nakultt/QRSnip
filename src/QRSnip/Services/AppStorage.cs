using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using QRSnip.Core;

namespace QRSnip.Services;

public sealed class AppSettings
{
    /// <summary>The global shortcut that starts a scan.</summary>
    public string Hotkey { get; set; } = "F10";

    /// <summary>Copy the decoded text to the clipboard as soon as a code is found.</summary>
    public bool AutoCopy { get; set; }

    /// <summary>Closing the window keeps QRSnip in the notification area so the hotkey keeps working.</summary>
    public bool RunInBackground { get; set; } = true;

    /// <summary>Remember scanned codes on this device.</summary>
    public bool KeepHistory { get; set; } = true;
}

public sealed class HistoryEntry
{
    public required string Text { get; init; }

    public required DateTimeOffset ScannedAt { get; init; }

    [JsonIgnore]
    public ContentInfo Content => _content ??= ContentClassifier.Classify(Text);

    [JsonIgnore]
    public string When => ScannedAt.LocalDateTime.Date == DateTime.Today
        ? ScannedAt.LocalDateTime.ToString("t", System.Globalization.CultureInfo.CurrentCulture)
        : ScannedAt.LocalDateTime.ToString("d MMM", System.Globalization.CultureInfo.CurrentCulture);

    [JsonIgnore]
    public string Glyph => ResultPresenter.Glyph(Content.Kind);

    [JsonIgnore]
    public string KindLabel => ResultPresenter.Label(Content.Kind);

    [JsonIgnore]
    public string OpenLabel => ResultPresenter.OpenLabel(Content.Kind);

    [JsonIgnore]
    public bool CanOpen => Content.LaunchUri is not null;

    [JsonIgnore]
    public bool HasSecret => !string.IsNullOrEmpty(Content.Secret);

    /// <summary>One-line preview for lists; never shows a Wi-Fi password.</summary>
    [JsonIgnore]
    public string Subtitle => Content.Kind switch
    {
        ContentKind.Wifi => HasSecret ? "Wi-Fi network · password saved" : "Open Wi-Fi network",
        ContentKind.Contact => "Contact card",
        _ => Text.ReplaceLineEndings(" "),
    };

    private ContentInfo? _content;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<HistoryEntry>))]
internal sealed partial class StorageJson : JsonSerializerContext;

/// <summary>Settings and history as small JSON files under %LOCALAPPDATA%\QRSnip.</summary>
public sealed class AppStorage
{
    private const int MaxHistory = 100;

    private readonly string _folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QRSnip");

    public AppStorage()
    {
        Settings = Read(SettingsPath, StorageJson.Default.AppSettings) ?? new AppSettings();
        var items = Settings.KeepHistory ? Read(HistoryPath, StorageJson.Default.ListHistoryEntry) : null;
        History = new ObservableCollection<HistoryEntry>(items ?? []);
    }

    public AppSettings Settings { get; }

    public ObservableCollection<HistoryEntry> History { get; }

    private string SettingsPath => Path.Combine(_folder, "settings.json");

    private string HistoryPath => Path.Combine(_folder, "history.json");

    public void SaveSettings()
    {
        Write(SettingsPath, Settings, StorageJson.Default.AppSettings);
        if (!Settings.KeepHistory)
        {
            ClearHistory();
        }
    }

    public void AddToHistory(IEnumerable<ScanResult> results)
    {
        if (!Settings.KeepHistory)
        {
            return;
        }

        foreach (var result in results.Reverse())
        {
            var existing = History.FirstOrDefault(h => h.Text == result.Text);
            if (existing is not null)
            {
                History.Remove(existing);
            }

            History.Insert(0, new HistoryEntry { Text = result.Text, ScannedAt = DateTimeOffset.Now });
        }

        while (History.Count > MaxHistory)
        {
            History.RemoveAt(History.Count - 1);
        }

        SaveHistory();
    }

    public void Remove(HistoryEntry entry)
    {
        History.Remove(entry);
        SaveHistory();
    }

    public void ClearHistory()
    {
        History.Clear();
        try
        {
            File.Delete(HistoryPath);
        }
        catch (IOException)
        {
        }
    }

    private void SaveHistory() => Write(HistoryPath, History.ToList(), StorageJson.Default.ListHistoryEntry);

    private static T? Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), type) : default;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    private void Write<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, type));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }
}
