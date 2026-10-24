using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Valve.VR;

namespace VRCFaceTracking.Services;

public class OpenVRService(ILogger<OpenVRService> logger)
{
    private const string AppKey = "benaclejames.vrcft";
    private const string Connected = "Saved in SteamVR. While it's on, VRCFaceTracking also quits when SteamVR does.";
    private const string NotRunning = "Saved in SteamVR. Start SteamVR to see or change it.";
    private const string HeadsetNotConnected = "Saved in SteamVR. Connect your headset to see or change it.";

    private CVRSystem? _system;
    private readonly Lock _initLock = new();
    // The SteamVR server it's connected to. The connection stops working when that one closes
    private int? _serverProcessId;
    // The SteamVR server that sent a quit event, which may take a few seconds to exit
    private int? _quitServerProcessId;
    private EVRInitError? _lastInitError;
    private CancellationTokenSource? _quitWatch;
    private Task? _steamVRWatch;

    /// <summary>
    /// Raised from a background thread when it connects to or disconnects from SteamVR, or its status changes
    /// </summary>
    public event Action? StatusChanged;

    public bool Initialize()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Status = "Only supported on Windows.";
            return false;
        }

        lock (_initLock)
        {
            WatchSteamVR();

            // The settings page and startup both initialize it. A second OpenVR.Init fails with
            // Init_ClientVersionAlreadyProvided, which marked it uninitialized and broke the auto start setting
            if (IsInitialized)
            {
                return true;
            }

            return InitializeOpenVR();
        }
    }

    private bool InitializeOpenVR()
    {
        // An app that connects while SteamVR shuts down is one more it has to wait for, or kill
        var server = SteamVRServerProcessId();
        if (server != null && server == _quitServerProcessId)
        {
            Status = NotRunning;
            return false;
        }

        try
        {
            EVRInitError error = EVRInitError.None;
            _system = OpenVR.Init(ref error, EVRApplicationType.VRApplication_Background);

            if (error != EVRInitError.None)
            {
                // Retried in the background, so only logged when it changes
                if (error != _lastInitError)
                {
                    logger.LogWarning("Failed to initialize OpenVR: {0}", error);
                }
                _lastInitError = error;
                Status = error switch
                {
                    EVRInitError.Init_NoServerForBackgroundApp => NotRunning,
                    // SteamVR is running, but it turns background apps away until a headset is connected
                    EVRInitError.Driver_WirelessHmdNotConnected or EVRInitError.Init_HmdNotFound
                        or EVRInitError.Init_HmdNotFoundPresenceFailed => HeadsetNotConnected,
                    _ => $"Saved in SteamVR, but VRCFaceTracking couldn't connect to it ({error}).",
                };
                IsInitialized = false;
                return false;
            }
            _lastInitError = null;

            var currentDirectory = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location);
            if (currentDirectory == null)
            {
                Shutdown();
                Status = "Saved in SteamVR, but VRCFaceTracking couldn't find its app manifest.";
                return false;
            }

            var fullManifestPath = Path.Combine(currentDirectory, "app.vrmanifest");
            var manifestRegisterResult = OpenVR.Applications.AddApplicationManifest(fullManifestPath, false);
            if (manifestRegisterResult != EVRApplicationError.None)
            {
                logger.LogWarning("Failed to register manifest: {0}", manifestRegisterResult);
                // Disconnected, or the next try would fail with Init_ClientVersionAlreadyProvided
                Shutdown();
                Status = $"Saved in SteamVR, but SteamVR didn't accept VRCFaceTracking's app manifest ({manifestRegisterResult}).";
                return false;
            }

            logger.LogInformation("Successfully initialized OpenVR");
            _serverProcessId = SteamVRServerProcessId();
            Status = Connected;
            IsInitialized = true;
            WatchForQuit();
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException)
        {
            logger.LogWarning("OpenVR native library not available: {Message}", ex.Message);
            Status = $"Saved in SteamVR, but VRCFaceTracking couldn't load OpenVR ({ex.Message}).";
            IsInitialized = false;
            return false;
        }
    }

    public void InitIfNotAlready()
    {
        if (!IsInitialized)
            Initialize();
    }

    /// <summary>
    /// Connects to SteamVR if it has started since the last try, and lets go of it if it has closed
    /// </summary>
    public void Refresh()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            var server = SteamVRServerProcessId();
            bool changed;
            lock (_initLock)
            {
                var (wasInitialized, oldStatus) = (IsInitialized, Status);

                if (IsInitialized && server != _serverProcessId)
                {
                    logger.LogInformation("SteamVR closed. Disconnecting from it");
                    Shutdown();
                }

                if (!IsInitialized)
                {
                    if (server == null)
                    {
                        Status = NotRunning;
                    }
                    else
                    {
                        InitializeOpenVR();
                    }
                }

                changed = IsInitialized != wasInitialized || Status != oldStatus;
            }

            if (changed)
            {
                StatusChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Couldn't check on SteamVR: {error}", ex.Message);
        }
    }

    // SteamVR can start, or the headset connect, after VRCFaceTracking does. The settings page checks more
    // often while it's open
    private void WatchSteamVR()
    {
        _steamVRWatch ??= Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync())
            {
                Refresh();
            }
        });
    }

    // SteamVR sends connected apps a quit event when it closes, and kills any that haven't exited 5 seconds later
    private void WatchForQuit()
    {
        var watch = new CancellationTokenSource();
        _quitWatch = watch;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(watch.Token))
                {
                    if (HandledQuit(watch.Token))
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning("Stopped watching for SteamVR quitting: {error}", ex.Message);
            }
        });
    }

    // Returns true once there's nothing left to watch
    private bool HandledQuit(CancellationToken watch)
    {
        lock (_initLock)
        {
            // Cancelled while waiting for the lock, so it may already be connected again with its own watch
            if (watch.IsCancellationRequested || !IsInitialized || _system == null)
            {
                return true;
            }

            var vrEvent = new VREvent_t();
            var size = (uint)Marshal.SizeOf<VREvent_t>();
            while (_system.PollNextEvent(ref vrEvent, size))
            {
                if (vrEvent.eventType != (uint)EVREventType.VREvent_Quit)
                {
                    continue;
                }

                // Started with SteamVR, so it goes with it. Otherwise it was started by hand, so it keeps running
                if (OpenVR.Applications.GetApplicationAutoLaunch(AppKey))
                {
                    logger.LogInformation("SteamVR is quitting, and auto start is on, so VRCFaceTracking is quitting too");
                    // Gives it longer than the 5 seconds to stop its modules
                    _system.AcknowledgeQuit_Exiting();
                    Dispatcher.UIThread.Post(() =>
                        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());
                }
                else
                {
                    logger.LogInformation("SteamVR is quitting. Disconnecting from it");
                    _quitServerProcessId = _serverProcessId;
                    Shutdown();
                    Status = NotRunning;
                    StatusChanged?.Invoke();
                }
                return true;
            }

            return false;
        }
    }

    private void Shutdown()
    {
        _quitWatch?.Cancel();
        _quitWatch = null;

        try
        {
            OpenVR.Shutdown();
        }
        catch (Exception ex)
        {
            logger.LogWarning("Couldn't disconnect from SteamVR: {error}", ex.Message);
        }

        _system = null;
        _serverProcessId = null;
        IsInitialized = false;
    }

    // Null when SteamVR isn't running
    private static int? SteamVRServerProcessId()
    {
        var processes = Process.GetProcessesByName("vrserver");
        try
        {
            return processes.Length == 0 ? null : processes.Min(p => p.Id);
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public bool IsInitialized { get; private set; }

    /// <summary>
    /// For the settings page: where auto start is saved, and why it can't be changed if it can't
    /// </summary>
    public string Status { get; private set; } = NotRunning;

    public bool AutoStart
    {
        get
        {
            lock (_initLock)
            {
                try
                {
                    return IsInitialized && OpenVR.Applications.GetApplicationAutoLaunch(AppKey);
                }
                catch
                {
                    return false;
                }
            }
        }
        set
        {
            if (!Initialize())
            {
                logger.LogWarning("Failed to set AutoStart preference. OpenVR couldn't be initialized.");
                return;
            }

            lock (_initLock)
            {
                try
                {
                    var result = OpenVR.Applications.SetApplicationAutoLaunch(AppKey, value);
                    if (result != EVRApplicationError.None)
                    {
                        logger.LogError("Failed to set auto launch: {0}", result);
                        Status = $"SteamVR didn't save the change ({result}).";
                        return;
                    }
                    Status = Connected;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Exception setting auto launch");
                    Status = $"SteamVR didn't save the change ({ex.Message}).";
                }
            }
        }
    }
}
