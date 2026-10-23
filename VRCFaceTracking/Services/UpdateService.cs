using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;
using VRCFaceTracking.Core.Contracts.Services;

namespace VRCFaceTracking.Services;

/// <summary>
/// Updates installed copies of VRCFaceTracking from a GitHub repo's releases, using Velopack
/// </summary>
public partial class UpdateService : ObservableObject
{
    /// <summary>
    /// The GitHub repo the release workflow built this copy in, so builds from a fork update from that fork.
    /// Empty for local builds
    /// </summary>
    public static readonly string BuildRepository = Assembly.GetExecutingAssembly()
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "UpdateRepository")?.Value ?? "";

    private readonly ILogger<UpdateService> _logger;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly Task _settingsLoad;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private UpdateInfo? _availableUpdate;
    private bool _loadingSettings;

    // Empty uses BuildRepository
    [ObservableProperty][property: SavedSetting("UpdateRepository", "")] private string _repository = "";
    [ObservableProperty][property: SavedSetting("InstallUpdatesAutomatically", true)] private bool _autoUpdate = true;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canDownload;
    [ObservableProperty] private bool _canRestart;

    public string DefaultRepository => BuildRepository;

    /// <summary>
    /// The version Velopack installed, or null when this copy wasn't installed with the installer
    /// </summary>
    public string? InstalledVersion { get; } =
        VelopackLocator.IsCurrentSet ? VelopackLocator.Current.CurrentlyInstalledVersion?.ToString() : null;

    private string EffectiveRepository => string.IsNullOrWhiteSpace(Repository) ? BuildRepository : Repository.Trim();

    // Takes owner/name or a full URL
    private string RepositoryUrl => EffectiveRepository.StartsWith("http", StringComparison.OrdinalIgnoreCase)
        ? EffectiveRepository
        : $"https://github.com/{EffectiveRepository}";

    public UpdateService(ILogger<UpdateService> logger, ILocalSettingsService localSettingsService)
    {
        _logger = logger;
        _localSettingsService = localSettingsService;
        _settingsLoad = LoadSettings();
    }

    private async Task LoadSettings()
    {
        _loadingSettings = true;
        try
        {
            await _localSettingsService.Load(this);
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    partial void OnRepositoryChanged(string value) => SaveSettings();
    partial void OnAutoUpdateChanged(bool value) => SaveSettings();

    private void SaveSettings()
    {
        if (!_loadingSettings)
        {
            _localSettingsService.Save(this);
        }
    }

    /// <summary>
    /// Looks for a newer release, and downloads it if updates install automatically. A downloaded update is applied the next time VRCFaceTracking starts
    /// </summary>
    public async Task CheckForUpdatesAsync()
    {
        await _settingsLoad;
        if (!await _updateLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (InstalledVersion == null)
            {
                await SetState("Updates only work in copies installed with the installer from the releases page.");
                return;
            }

            if (string.IsNullOrEmpty(EffectiveRepository))
            {
                await SetState("Set an update source to get updates.");
                return;
            }

            await SetState($"Checking {EffectiveRepository} for updates...", busy: true);
            var update = await CreateManager().CheckForUpdatesAsync();
            if (update == null)
            {
                await SetState($"Up to date ({InstalledVersion}).");
                return;
            }

            _availableUpdate = update;
            var version = update.TargetFullRelease.Version;
            _logger.LogInformation("Update {version} is available from {repository}", version, EffectiveRepository);
            if (!AutoUpdate)
            {
                await SetState($"Version {version} is available.", canDownload: true);
                return;
            }

            await Download(update);
        }
        catch (Exception e)
        {
            _logger.LogWarning("Failed to check for updates from {repository}: {error}", EffectiveRepository, e.Message);
            await SetState($"Couldn't check for updates: {e.Message}");
        }
        finally
        {
            _updateLock.Release();
        }
    }

    public async Task DownloadUpdateAsync()
    {
        if (_availableUpdate == null || !await _updateLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            await Download(_availableUpdate);
        }
        catch (Exception e)
        {
            _logger.LogWarning("Failed to download update {version}: {error}", _availableUpdate.TargetFullRelease.Version, e.Message);
            await SetState($"Couldn't download the update: {e.Message}", canDownload: true);
        }
        finally
        {
            _updateLock.Release();
        }
    }

    private async Task Download(UpdateInfo update)
    {
        var version = update.TargetFullRelease.Version;
        await SetState($"Downloading version {version}...", busy: true);
        await CreateManager().DownloadUpdatesAsync(update);

        _logger.LogInformation("Downloaded update {version}. It installs on the next start", version);
        await SetState($"Version {version} installs the next time VRCFaceTracking starts.", canRestart: true);
    }

    /// <summary>
    /// Closes VRCFaceTracking the usual way so modules shut down, then installs the downloaded update and starts it again
    /// </summary>
    public void RestartToUpdate()
    {
        if (_availableUpdate == null)
        {
            return;
        }

        CreateManager().WaitExitThenApplyUpdates(_availableUpdate.TargetFullRelease, silent: true, restart: true);
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private UpdateManager CreateManager() => new(new GithubSource(RepositoryUrl, null, false));

    private Task SetState(string status, bool busy = false, bool canDownload = false, bool canRestart = false) =>
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            Status = status;
            IsBusy = busy;
            CanDownload = canDownload;
            CanRestart = canRestart;
        }).GetTask();
}
