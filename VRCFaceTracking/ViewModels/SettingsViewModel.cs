using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Models;
using VRCFaceTracking.Services;

namespace VRCFaceTracking.ViewModels;

public partial class SettingsViewModel : ObservableRecipient
{
    [ObservableProperty] private List<GithubContributor> _contributors = [];

    public TrayService Tray { get; }
    public IOscTarget OscTarget { get; }
    public RiskySettingsViewModel RiskySettings { get; }
    public UpdateService Updates { get; }

    private readonly OpenVRService _openVRService;

    private bool _refreshingSteamVR;

    public bool AutoStart
    {
        get => _openVRService.AutoStart;
        set
        {
            _openVRService.AutoStart = value;
            // Read back once the binding is done, so the switch flips back if SteamVR didn't save it
            Dispatcher.UIThread.Post(RaiseSteamVRChanged);
        }
    }

    public bool IsOpenVREnabled => _openVRService.IsInitialized;

    public string AutoStartStatus => _openVRService.Status;

    public SettingsViewModel(
        GithubService githubService,
        OpenVRService openVRService,
        TrayService trayService,
        IOscTarget oscTarget,
        RiskySettingsViewModel riskySettingsViewModel,
        UpdateService updateService)
    {
        _openVRService = openVRService;
        Tray = trayService;
        OscTarget = oscTarget;
        RiskySettings = riskySettingsViewModel;
        Updates = updateService;

        _openVRService.InitIfNotAlready();
        _openVRService.StatusChanged += () => Dispatcher.UIThread.Post(RaiseSteamVRChanged);
        LoadContributors(githubService);
    }

    /// <summary>
    /// Picks up SteamVR starting or closing. The settings page calls it while it's open
    /// </summary>
    public async Task RefreshSteamVRAsync()
    {
        if (_refreshingSteamVR)
        {
            return;
        }

        _refreshingSteamVR = true;
        try
        {
            await Task.Run(_openVRService.Refresh);
        }
        finally
        {
            _refreshingSteamVR = false;
        }
        RaiseSteamVRChanged();
    }

    private void RaiseSteamVRChanged()
    {
        OnPropertyChanged(nameof(IsOpenVREnabled));
        OnPropertyChanged(nameof(AutoStart));
        OnPropertyChanged(nameof(AutoStartStatus));
    }

    [RelayCommand]
    private Task CheckForUpdates() => Updates.CheckForUpdatesAsync();

    [RelayCommand]
    private Task DownloadUpdate() => Updates.DownloadUpdateAsync();

    [RelayCommand]
    private void RestartToUpdate() => Updates.RestartToUpdate();

    private async void LoadContributors(GithubService githubService)
    {
        try
        {
            Contributors = await githubService.GetContributors("benaclejames/VRCFaceTracking");
        }
        catch
        {
        }
    }
}
