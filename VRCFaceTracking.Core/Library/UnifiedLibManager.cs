using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Contracts.Services;
using VRCFaceTracking.Core.Models;
using VRCFaceTracking.Core.Sandboxing;
using VRCFaceTracking.Core.Services;

namespace VRCFaceTracking.Core.Library;

public partial class UnifiedLibManager : ILibManager
{
    private readonly ILogger<UnifiedLibManager> _logger;
    private readonly ILogger _moduleLogger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IDispatcherService _dispatcherService;
    private readonly IModuleDataService _moduleDataService;
    private readonly SendCoordinator _sendCoordinator;
    private readonly TrackingSourceSettings _trackingSources;
    private readonly Task _trackingSourcesLoad;

    // Assembly names of the modules spawned by the last Initialize, so a source preference for a module that isn't installed is ignored
    private readonly HashSet<string> _spawnedModuleNames = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<ModuleMetadataInternal> LoadedModulesMetadata { get; set; }

    public static ModuleState EyeStatus { get; private set; }
    public static ModuleState ExpressionStatus { get; private set; }

    // Sandbox stuff
    private readonly string _sandboxProcessPath;
    private readonly List<ModuleRuntimeInfo> AvailableSandboxModules = new();
    private readonly List<ModuleRuntimeInfo> _moduleThreads = new();
    private static VrcftSandboxServer _sandboxServer;

    public UnifiedLibManager(ILoggerFactory factory, IDispatcherService dispatcherService, IModuleDataService moduleDataService, SendCoordinator sendCoordinator,
        ILocalSettingsService localSettingsService, TrackingSourceSettings trackingSources)
    {
        _loggerFactory = factory;
        _logger = factory.CreateLogger<UnifiedLibManager>();
        _moduleLogger = factory.CreateLogger("\0VRCFT\0");
        _dispatcherService = dispatcherService;
        _moduleDataService = moduleDataService;
        _sendCoordinator = sendCoordinator;
        _trackingSources = trackingSources;

        // Started here rather than in Initialize, which can run seconds later, so a choice made in the UI before then isn't overwritten
        _trackingSourcesLoad = localSettingsService.Load(_trackingSources);

        LoadedModulesMetadata = new ObservableCollection<ModuleMetadataInternal>();
        _sandboxProcessPath = Path.GetFullPath(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "VRCFaceTracking.ModuleProcess.exe" : "VRCFaceTracking.ModuleProcess");
        if ( !File.Exists(_sandboxProcessPath) )
        {
            // @TODO: Better error handling
            throw new FileNotFoundException($"Failed to find sandbox process at \"{_sandboxProcessPath}\"!");
        }

        // @TODO: Kill any lingering sub-modules to eliminate any conflicts
    }

    public async Task Initialize()
    {
        LoadedModulesMetadata.Clear();
        LoadedModulesMetadata.Add(new ModuleMetadataInternal
        { 
            Active = false,
            Name = "Initializing Modules..."
        });

        // Spawn sandbox server if it's null
        if (_sandboxServer == null )
        {
            // @TODO: Figure out an elegant way to ask the GUI for the ports the user assigned to the OSCTarget.
            var reservedPorts = new[] { 9000, 9001 };
            _sandboxServer = new VrcftSandboxServer(_loggerFactory, reservedPorts);
            _sandboxServer.OnPacketReceived += OnSandboxPacketReceived;
        }

        _logger.LogInformation("Starting initialization tracking");

        await TeardownAllModules();

        await _trackingSourcesLoad;

        var modules = _moduleDataService.GetInstalledModules().Concat(_moduleDataService.GetLegacyModules());
        var modulePaths = modules.Select(m => m.AssemblyLoadPath).ToArray();

        _spawnedModuleNames.Clear();
        _spawnedModuleNames.UnionWith(modulePaths.Select(Path.GetFileNameWithoutExtension).OfType<string>());
        _logger.LogInformation("Tracking sources. Eye: {eye} Expr: {expr}",
            DescribeSource(_trackingSources.EyeSource), DescribeSource(_trackingSources.ExpressionSource));

        AvailableSandboxModules.Clear();
        InitialiseSandboxesBaseOnPaths(modulePaths);

        if (AvailableSandboxModules.Count > 0)
        {
            _logger.LogDebug("Initializing requested runtimes...");
            return;
        }

        _dispatcherService.Run(() =>
        {
            LoadedModulesMetadata.Clear();
            LoadedModulesMetadata.Add(new ModuleMetadataInternal
            {
                Active = false,
                Name = "No Modules Loaded",
            });
        });
        _logger.LogWarning("No modules loaded.");
    }

    // Signal all active modules to gracefully shut down their respective runtimes.
    public async Task TeardownAllModules()
    {
        _logger.LogInformation("Tearing down all modules...");

        foreach (var module in _moduleThreads)
        {
            await TryTeardownModule(module);
        }
        _moduleThreads.Clear();

        foreach (var module in AvailableSandboxModules)
        {
            await TryTeardownModule(module);
        }
        AvailableSandboxModules.Clear();

        EyeStatus = ModuleState.Uninitialized;
        ExpressionStatus = ModuleState.Uninitialized;
    }

    // A module may take a part of the face if the user left it on automatic, picked this module, or picked a module that isn't installed
    private bool IsAllowedSource(string preferredSource, ModuleRuntimeInfo module) =>
        preferredSource == TrackingSourceSettings.Automatic
        || !_spawnedModuleNames.Contains(preferredSource)
        || string.Equals(preferredSource, module.ModuleClassName, StringComparison.OrdinalIgnoreCase);

    private string DescribeSource(string preferredSource) => preferredSource switch
    {
        TrackingSourceSettings.Automatic => "automatic",
        _ when !_spawnedModuleNames.Contains(preferredSource) => $"{preferredSource} (not installed, using automatic)",
        _ => preferredSource,
    };

    private async Task TryTeardownModule(ModuleRuntimeInfo module)
    {
        if (module == null || (module.Process?.HasExited ?? true))
        {
            return;
        }

        var success = false;
        try
        {
            success = await TeardownModuleSandboxed(module);
        }
        finally
        {
            if (!success)
            {
                var moduleName = module.ModuleInformation?.Name ?? module.ModuleClassName ?? "Unknown";
                _logger.LogWarning($"Module: {moduleName} failed to shut down. Killing its thread.");
                module.UpdateThread?.Interrupt();
            }
        }
    }
}
