using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using FluentAvalonia.Styling;
using VRCFaceTracking.Contracts;
using VRCFaceTracking.ViewModels;

namespace VRCFaceTracking.Views;

public partial class SettingsPage : UserControl, INotifyNavigated
{
    private SettingsViewModel ViewModel => (SettingsViewModel)DataContext!;

    // Picks up SteamVR starting or closing while the page is open
    private readonly DispatcherTimer _steamVRTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public SettingsPage()
    {
        InitializeComponent();
        DataContext = Ioc.Default.GetRequiredService<SettingsViewModel>();
        _steamVRTimer.Tick += SteamVRTimer_Tick;

        // Show current version
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Version {version?.ToString(3) ?? "Unknown"}";

        var faTheme = Application.Current?.Styles.OfType<FluentAvaloniaTheme>().FirstOrDefault();
        if (faTheme != null)
        {
            ThemeCombo.SelectedIndex = Application.Current?.RequestedThemeVariant?.Key?.ToString() switch
            {
                "Light" => 0,
                "Dark" => 1,
                _ => 2
            };
        }
    }

    public void OnNavigatedTo()
    {
        _ = ViewModel.RefreshSteamVRAsync();
        _steamVRTimer.Start();
    }

    private async void SteamVRTimer_Tick(object? sender, EventArgs e)
    {
        // Navigated to another page
        if (!IsVisible)
        {
            _steamVRTimer.Stop();
            return;
        }

        // Closed to the tray
        if (TopLevel.GetTopLevel(this) is not { IsVisible: true })
        {
            return;
        }

        await ViewModel.RefreshSteamVRAsync();
    }

    private void ThemeCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ThemeCombo.SelectedItem is not ComboBoxItem item) return;

        Application.Current!.RequestedThemeVariant = item.Tag?.ToString() switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    private void ForceReInit_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.RiskySettings.ForceReInit();
    }

    private void ResetVRCFT_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.RiskySettings.ResetVRCFT();
    }

    private void ResetAvatarConfig_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.RiskySettings.ResetAvatarOscManifests();
    }

    private async void ContributorButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && !string.IsNullOrEmpty(url))
        {
            try
            {
                var launcher = TopLevel.GetTopLevel(this)?.Launcher;
                if (launcher != null)
                    await launcher.LaunchUriAsync(new Uri(url));
            }
            catch { }
        }
    }
}
