using System.Net.Http.Headers;
using System.Text.Json;
using QRSnip.Core;
using QRSnip.Native;
using Velopack;
using Velopack.Sources;
using Windows.ApplicationModel;
using Windows.Services.Store;
using WinRT.Interop;

namespace QRSnip.Services;

/// <summary>Where this copy of QRSnip was installed from, which decides how it is updated.</summary>
internal enum UpdateChannel
{
    /// <summary>Microsoft Store: updated through the Store.</summary>
    Store,

    /// <summary>The GitHub installer (Velopack): downloads and applies updates itself.</summary>
    GitHub,

    /// <summary>Portable zip (or a sideloaded package): only told about new versions.</summary>
    Portable,
}

/// <summary>A newer version that can be installed (or downloaded).</summary>
internal sealed record AvailableUpdate(string Version, string Notes, bool Mandatory, Uri Page);

internal enum UpdateCheckResult
{
    UpToDate,
    Available,
    Failed,
}

/// <summary>
/// Checks for and installs updates from the channel QRSnip was installed from. Windows only
/// lets a package be updated from its own channel, so a Store install never takes a GitHub
/// build and vice versa. Checks fail quietly; the caller decides what to show.
/// </summary>
internal sealed class UpdateService
{
    public const string RepoUrl = "https://github.com/nakultt/QRSnip";
    private const string LatestReleaseApi = "https://api.github.com/repos/nakultt/QRSnip/releases/latest";
    private static readonly Uri ReleasesPage = new($"{RepoUrl}/releases/latest");

    private readonly Func<nint> _ownerWindow;
    private readonly UpdateManager? _velopack;
    private UpdateInfo? _velopackUpdate;
    private IReadOnlyList<StorePackageUpdate>? _storeUpdates;

    public UpdateService(Func<nint> ownerWindow)
    {
        _ownerWindow = ownerWindow;
        CurrentVersion = typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);

        if (StartupService.IsPackaged)
        {
            Channel = IsStoreSigned() ? UpdateChannel.Store : UpdateChannel.Portable;
            return;
        }

        try
        {
            var manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
            if (manager.IsInstalled)
            {
                _velopack = manager;
                Channel = UpdateChannel.GitHub;
                return;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }

        Channel = UpdateChannel.Portable;
    }

    public UpdateChannel Channel { get; }

    public Version CurrentVersion { get; }

    public string ChannelName => Channel switch
    {
        UpdateChannel.Store => "Microsoft Store",
        UpdateChannel.GitHub => "Installed from GitHub",
        _ => "Portable",
    };

    /// <summary>True when an update can be installed from inside QRSnip (otherwise it is a download link).</summary>
    public bool CanInstall => Channel is UpdateChannel.Store or UpdateChannel.GitHub;

    public AvailableUpdate? Available { get; private set; }

    public bool IsBusy { get; private set; }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return Available is null ? UpdateCheckResult.UpToDate : UpdateCheckResult.Available;
        }

        IsBusy = true;
        try
        {
            Available = Channel switch
            {
                UpdateChannel.Store => await CheckStoreAsync(cancellationToken),
                UpdateChannel.GitHub => await CheckVelopackAsync(cancellationToken),
                _ => await CheckGitHubReleaseAsync(cancellationToken),
            };
            return Available is null ? UpdateCheckResult.UpToDate : UpdateCheckResult.Available;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Offline, rate-limited, Store unavailable...: try again next time.
            System.Diagnostics.Debug.WriteLine(ex);
            return UpdateCheckResult.Failed;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Downloads and installs <see cref="Available"/>, then restarts QRSnip. For portable copies,
    /// opens the release page instead. Returns false if it didn't happen (cancelled or failed).
    /// </summary>
    /// <param name="beforeRestart">Called right before the process restarts (remove the tray icon etc.).</param>
    public async Task<bool> InstallAsync(IProgress<int>? progress, Action<bool> beforeRestart, bool restartInBackground)
    {
        if (Available is not { } update || IsBusy)
        {
            return false;
        }

        IsBusy = true;
        try
        {
            switch (Channel)
            {
                case UpdateChannel.GitHub when _velopack is not null && _velopackUpdate is not null:
                    await _velopack.DownloadUpdatesAsync(_velopackUpdate, p => progress?.Report(p));
                    beforeRestart(restartInBackground);
                    _velopack.ApplyUpdatesAndRestart(_velopackUpdate.TargetFullRelease, restartInBackground ? ["--background"] : null);
                    return true;

                case UpdateChannel.Store when _storeUpdates is { Count: > 0 } updates:
                    var context = StoreContext.GetDefault();
                    InitializeWithWindow.Initialize(context, _ownerWindow());

                    // Windows closes QRSnip to install the package; ask it to start us again.
                    _ = Win32.RegisterApplicationRestart(restartInBackground ? "--background" : null, 0);
                    var operation = context.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);
                    operation.Progress = (_, status) => progress?.Report((int)(status.TotalDownloadProgress * 100));
                    var result = await operation;
                    return result.OverallState == StorePackageUpdateState.Completed;

                default:
                    await Windows.System.Launcher.LaunchUriAsync(update.Page);
                    return true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static bool IsStoreSigned()
    {
        try
        {
            return Package.Current.SignatureKind == PackageSignatureKind.Store;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    private async Task<AvailableUpdate?> CheckStoreAsync(CancellationToken cancellationToken)
    {
        var context = StoreContext.GetDefault();
        var updates = await context.GetAppAndOptionalStorePackageUpdatesAsync().AsTask(cancellationToken);
        _storeUpdates = updates;
        if (updates.Count == 0)
        {
            return null;
        }

        var v = updates[0].Package.Id.Version;
        var version = $"{v.Major}.{v.Minor}.{v.Build}";

        // The Store doesn't hand out release notes; use the matching GitHub release's, if any.
        var notes = string.Empty;
        try
        {
            using var release = await GetJsonAsync($"https://api.github.com/repos/nakultt/QRSnip/releases/tags/v{version}", cancellationToken);
            notes = ReleaseInfo.ToPlainText(release.RootElement.GetProperty("body").GetString());
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException)
        {
        }

        return new AvailableUpdate(version, notes, updates.Any(u => u.Mandatory), new Uri($"{RepoUrl}/releases/tag/v{version}"));
    }

    private async Task<AvailableUpdate?> CheckVelopackAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _velopackUpdate = await _velopack!.CheckForUpdatesAsync();
        if (_velopackUpdate is null)
        {
            return null;
        }

        var target = _velopackUpdate.TargetFullRelease;
        var version = target.Version.ToString();
        return new AvailableUpdate(version, ReleaseInfo.ToPlainText(target.NotesMarkdown), false, new Uri($"{RepoUrl}/releases/tag/v{version}"));
    }

    private async Task<AvailableUpdate?> CheckGitHubReleaseAsync(CancellationToken cancellationToken)
    {
        using var release = await GetJsonAsync(LatestReleaseApi, cancellationToken);
        var root = release.RootElement;
        var tag = root.GetProperty("tag_name").GetString();
        if (!ReleaseInfo.TryParseVersion(tag, out var version) || !ReleaseInfo.IsNewer(tag, CurrentVersion))
        {
            return null;
        }

        var page = root.TryGetProperty("html_url", out var url) && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
            ? u
            : ReleasesPage;
        var notes = root.TryGetProperty("body", out var body) ? ReleaseInfo.ToPlainText(body.GetString()) : string.Empty;
        return new AvailableUpdate(version.ToString(3), notes, false, page);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("QRSnip", CurrentVersion.ToString(3)));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.GetAsync(new Uri(url), cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}
