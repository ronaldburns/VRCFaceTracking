using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Contracts.Services;

namespace VRCFaceTracking.Services;

/// <summary>
/// Puts VRCFaceTracking in the notification area, so it can keep running there with its window closed
/// </summary>
public partial class TrayService : ObservableObject
{
    private readonly ILogger<TrayService> _logger;
    private readonly ILocalSettingsService _localSettingsService;
    private bool _loadingSettings;

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private Window? _window;
    private TrayIcon? _trayIcon;

    [ObservableProperty][property: SavedSetting("CloseToTray", false)] private bool _closeToTray;
    [ObservableProperty][property: SavedSetting("StartMinimized", false)] private bool _startMinimized;

    public TrayService(ILogger<TrayService> logger, ILocalSettingsService localSettingsService)
    {
        _logger = logger;
        _localSettingsService = localSettingsService;
    }

    public async Task LoadSettingsAsync()
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

    partial void OnCloseToTrayChanged(bool value) => SaveSettings();
    partial void OnStartMinimizedChanged(bool value) => SaveSettings();

    private void SaveSettings()
    {
        if (!_loadingSettings)
        {
            _localSettingsService.Save(this);
        }
    }

    /// <summary>
    /// Adds the tray icon for the main window. Returns false if it couldn't be added, so the window must not be hidden
    /// </summary>
    public bool Attach(IClassicDesktopStyleApplicationLifetime desktop, Window window)
    {
        _desktop = desktop;
        _window = window;

        try
        {
            if (window.Icon == null || Application.Current == null)
            {
                _logger.LogWarning("Couldn't add the tray icon: the window has no icon");
                return false;
            }

            var open = new NativeMenuItem("Open VRCFaceTracking");
            open.Click += (_, _) => ShowWindow();
            var quit = new NativeMenuItem("Quit");
            quit.Click += (_, _) => desktop.Shutdown();

            _trayIcon = new TrayIcon
            {
                Icon = window.Icon,
                ToolTipText = "VRCFaceTracking",
                Menu = new NativeMenu { Items = { open, new NativeMenuItemSeparator(), quit } },
            };
            _trayIcon.Clicked += (_, _) => ShowWindow();
            TrayIcon.SetIcons(Application.Current, new TrayIcons { _trayIcon });
        }
        catch (Exception e)
        {
            _logger.LogWarning("Couldn't add the tray icon: {error}", e.Message);
            _trayIcon = null;
            return false;
        }

        window.Closing += OnWindowClosing;
        return true;
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // Only the close button. Quitting from the tray, restarting to update and Windows shutting down still close it
        if (CloseToTray && e.CloseReason == WindowCloseReason.WindowClosing && !e.IsProgrammatic)
        {
            e.Cancel = true;
            _window?.Hide();
        }
    }

    public void ShowWindow()
    {
        if (_window == null)
        {
            return;
        }

        // Left unset when starting minimized, since the lifetime shows its main window on startup
        if (_desktop != null)
        {
            _desktop.MainWindow ??= _window;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }
        _window.Activate();
    }
}
