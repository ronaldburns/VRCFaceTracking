using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices.Swift;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using VRCFaceTracking.Contracts;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Models;
using VRCFaceTracking.Core.OSC;
using VRCFaceTracking.Core.Services;
using VRCFaceTracking.Models;

namespace VRCFaceTracking.ViewModels;

public partial class MainViewModel : ObservableRecipient
{
    public ILibManager LibManager { get; }
    public OscQueryService ParameterOutputService { get; }
    public OscRecvService OscRecvService { get; }
    public OscSendService OscSendService { get; }
    public IOscTarget OscTarget { get; }
    private readonly IModuleDataService _moduleDataService;
    private readonly TrackingSourceSettings _trackingSources;

    private static readonly TrackingSourceOption AutomaticSource = new(TrackingSourceSettings.Automatic, "Automatic");
    private const string NoActiveSource = "Not handled by any module";

    [ObservableProperty] private List<TrackingSourceOption> _trackingSourceOptions = [AutomaticSource];
    [ObservableProperty] private TrackingSourceOption? _selectedEyeSource = AutomaticSource;
    [ObservableProperty] private TrackingSourceOption? _selectedExpressionSource = AutomaticSource;
    [ObservableProperty] private string _activeEyeSource = NoActiveSource;
    [ObservableProperty] private string _activeExpressionSource = NoActiveSource;

    // Set while the picker selection is being synced from settings, so it doesn't count as a user change
    private bool _syncingTrackingSources;
    private readonly SemaphoreSlim _reinitLock = new(1, 1);

    private int _messagesRecvd;
    [ObservableProperty] private int _messagesInPerSec;

    private int _messagesSent;
    [ObservableProperty] private int _messagesOutPerSec;

    [ObservableProperty] private bool _noModulesInstalled;
    
    [ObservableProperty] private bool _oscWasDisabled;

    private DispatcherTimer msgCounterTimer;

    public MainViewModel(
        ILibManager libManager,
        OscQueryService parameterOutputService,
        IModuleDataService moduleDataService,
        IOscTarget oscTarget,
        OscRecvService oscRecvService,
        OscSendService oscSendService,
        TrackingSourceSettings trackingSources
        )
    {
        //Services
        LibManager = libManager;
        ParameterOutputService = parameterOutputService;
        OscTarget = oscTarget;
        OscRecvService = oscRecvService;
        OscSendService = oscSendService;
        _moduleDataService = moduleDataService;
        _trackingSources = trackingSources;

        // Tracking sources. Saved settings load in the background, so resync the pickers whenever they change
        _trackingSources.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(SyncTrackingSourceSelection);
        LibManager.LoadedModulesMetadata.CollectionChanged += OnLoadedModulesChanged;

        // Message Timer
        OscRecvService.OnMessageReceived += MessageReceived;
        OscSendService.OnMessagesDispatched += MessageDispatched;
        msgCounterTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        msgCounterTimer.Tick += (_, _) =>
        {
            MessagesInPerSec = _messagesRecvd;
            _messagesRecvd = 0;
            
            MessagesOutPerSec = _messagesSent;
            _messagesSent = 0;
        };
        msgCounterTimer.Start();
        
        OnNavigatedTo();
    }

    public void OnNavigatedTo()
    {
        // Modules
        var installedNewModules = _moduleDataService.GetInstalledModules();
        var installedLegacyModules = _moduleDataService.GetLegacyModules().Count();
        NoModulesInstalled = !installedNewModules.Any() && installedLegacyModules == 0;

        // Tracking sources
        SyncTrackingSourceSelection();
        RefreshActiveTrackingSources();
    }

    private void SyncTrackingSourceSelection()
    {
        var options = _moduleDataService.GetInstalledModules().Concat(_moduleDataService.GetLegacyModules())
            .Select(m => new TrackingSourceOption(Path.GetFileNameWithoutExtension(m.DllFileName), m.ModuleName))
            .DistinctBy(o => o.ModuleName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(o => o.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Prepend(AutomaticSource)
            .ToList();

        // Keep showing a saved choice whose module was uninstalled. Modules treat it as automatic until it's back
        foreach (var saved in new[] { _trackingSources.EyeSource, _trackingSources.ExpressionSource })
        {
            if (!options.Any(o => string.Equals(o.ModuleName, saved, StringComparison.OrdinalIgnoreCase)))
            {
                options.Add(new TrackingSourceOption(saved, $"{saved} (not installed)"));
            }
        }

        // Swapping the list can make the pickers clear their selection, so it's set again after
        _syncingTrackingSources = true;
        try
        {
            if (!options.SequenceEqual(TrackingSourceOptions))
            {
                TrackingSourceOptions = options;
            }
            SelectedEyeSource = options.First(o => string.Equals(o.ModuleName, _trackingSources.EyeSource, StringComparison.OrdinalIgnoreCase));
            SelectedExpressionSource = options.First(o => string.Equals(o.ModuleName, _trackingSources.ExpressionSource, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _syncingTrackingSources = false;
        }
    }

    partial void OnSelectedEyeSourceChanged(TrackingSourceOption? value)
    {
        if (_syncingTrackingSources || value == null || value.ModuleName == _trackingSources.EyeSource)
        {
            return;
        }

        _trackingSources.EyeSource = value.ModuleName;
        _ = ReinitializeModulesAsync();
    }

    partial void OnSelectedExpressionSourceChanged(TrackingSourceOption? value)
    {
        if (_syncingTrackingSources || value == null || value.ModuleName == _trackingSources.ExpressionSource)
        {
            return;
        }

        _trackingSources.ExpressionSource = value.ModuleName;
        _ = ReinitializeModulesAsync();
    }

    // Modules decide what to start up when they're initialized, so a new source only takes effect after a restart
    private async Task ReinitializeModulesAsync()
    {
        await _reinitLock.WaitAsync();
        try
        {
            await LibManager.Initialize();
        }
        finally
        {
            _reinitLock.Release();
        }
    }

    private void OnLoadedModulesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var module in e.NewItems?.OfType<ModuleMetadataInternal>() ?? [])
        {
            module.PropertyChanged += OnLoadedModulePropertyChanged;
        }
        foreach (var module in e.OldItems?.OfType<ModuleMetadataInternal>() ?? [])
        {
            module.PropertyChanged -= OnLoadedModulePropertyChanged;
        }

        Dispatcher.UIThread.Post(RefreshActiveTrackingSources);
    }

    // Raised from the module IPC thread
    private void OnLoadedModulePropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        Dispatcher.UIThread.Post(RefreshActiveTrackingSources);

    private void RefreshActiveTrackingSources()
    {
        var modules = LibManager.LoadedModulesMetadata.ToList();
        ActiveEyeSource = DescribeActiveSource(modules.FirstOrDefault(m => m.UsingEye));
        ActiveExpressionSource = DescribeActiveSource(modules.FirstOrDefault(m => m.UsingExpression));
    }

    private static string DescribeActiveSource(ModuleMetadataInternal? module) => module switch
    {
        null => NoActiveSource,
        { Active: false } => $"{module.Name} (paused)",
        _ => $"Handled by {module.Name}",
    };

    private void MessageReceived(OscMessage msg) => _messagesRecvd++;
    private void MessageDispatched(int msgCount) => _messagesSent += msgCount;

    ~MainViewModel()
    {
        OscRecvService.OnMessageReceived -= MessageReceived;
        OscSendService.OnMessagesDispatched -= MessageDispatched;
        
        msgCounterTimer.Stop();
    }
}
