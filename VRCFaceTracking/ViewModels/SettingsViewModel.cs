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

    public bool AutoStart
    {
        get => _openVRService.AutoStart;
        set
        {
            _openVRService.AutoStart = value;
            OnPropertyChanged();
        }
    }

    public bool IsOpenVREnabled => _openVRService.IsInitialized;

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
        LoadContributors(githubService);
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
