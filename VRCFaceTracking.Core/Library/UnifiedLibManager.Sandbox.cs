using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using VRCFaceTracking.Core.Sandboxing;
using VRCFaceTracking.Core.Sandboxing.IPC;

namespace VRCFaceTracking.Core.Library;

public partial class UnifiedLibManager
{
    private void OnSandboxPacketReceived(in IpcPacket packet, in int port)
    {
        // Special cases for modules we don't know of yet
        var packetType = packet.GetPacketType();
        if (packetType == IpcPacket.PacketType.Handshake)
        {
            HandleHandshake((HandshakePacket)packet, port);
            return;
        }
        if (packetType == IpcPacket.PacketType.EventLog)
        {
            _moduleLogger.Log(((EventLogPacket)packet).LogLevel, ((EventLogPacket)packet).Message);
            return;
        }
        
        var moduleIndex = FindModuleIndexByPort(port);
        if (moduleIndex == null)
        {
            return;
        }

        switch (packet.GetPacketType())
        {
            case IpcPacket.PacketType.ReplyGetSupported:
                HandleReplyGetSupported((ReplySupportedPacket)packet, port, moduleIndex.Value);
                break;

            case IpcPacket.PacketType.ReplyInit:
                HandleReplyInit((ReplyInitPacket)packet, port, moduleIndex.Value);
                break;

            case IpcPacket.PacketType.ReplyUpdate:
                HandleReplyUpdate((ReplyUpdatePacket)packet, moduleIndex.Value);
                break;
        }
    }

    private int? FindModuleIndexByPort(int port)
    {
        for (var i = 0; i < AvailableSandboxModules.Count; i++)
        {
            if (AvailableSandboxModules[i].SandboxProcessPort == port)
            {
                return i;
            }
        }
        
        return null;
    }

    private void HandleHandshake(HandshakePacket pkt, int port)
    {
        lock (AvailableSandboxModules)
        {
            // Look for a module we already spawned with this PID.
            for (var i = 0; i < AvailableSandboxModules.Count; i++)
            {
                if (AvailableSandboxModules[i].SandboxProcessPID != pkt.PID)
                {
                    continue;
                }

                var existing = AvailableSandboxModules[i];
                existing.SandboxProcessPort = port;
                AvailableSandboxModules[i] = existing;

                _logger.LogInformation("Initializing {module}...", existing.ModuleClassName);
                AttemptSandboxedModuleInitialize(existing);
                return;
            }

            var runtimeInfo = new ModuleRuntimeInfo
            {
                SandboxProcessPID  = pkt.PID,
                SandboxProcessPort = port,
                SandboxModulePath  = pkt.ModulePath,
                IsActive           = true,
                Process            = Process.GetProcessById(pkt.PID),
                ModuleClassName    = Path.GetFileNameWithoutExtension(pkt.ModulePath),
                ModuleInformation  = new(),
                EventBus           = new(),
            };
            AvailableSandboxModules.Add(runtimeInfo);

            _logger.LogInformation("Initializing {module}...", runtimeInfo.ModuleClassName);
            AttemptSandboxedModuleInitialize(runtimeInfo);
        }
    }

    private void HandleReplyGetSupported(ReplySupportedPacket reply, int port, int moduleIndex)
    {
        var module = AvailableSandboxModules[moduleIndex];
        module.SupportsEyeTracking        = module.SupportsEyeTracking        && reply.eyeAvailable;
        module.SupportsExpressionTracking = module.SupportsExpressionTracking && reply.expressionAvailable;

        module.EyeOffered        = EyeStatus == ModuleState.Uninitialized        && IsAllowedSource(_trackingSources.EyeSource, module);
        module.ExpressionOffered = ExpressionStatus == ModuleState.Uninitialized && IsAllowedSource(_trackingSources.ExpressionSource, module);

        // A module with nothing to track would otherwise initialize anyway and hold its device or port for nothing.
        // Killed rather than sent a teardown, because modules don't expect Teardown before Initialize.
        if (!module.EyeOffered && !module.ExpressionOffered)
        {
            _logger.LogInformation("{module} isn't picked for eye or face tracking, or both are taken. Stopping it", module.ModuleClassName);
            module.IsActive = false;
            try
            {
                module.Process?.Kill(entireProcessTree: true);
            }
            catch (Exception e)
            {
                _logger.LogWarning("Couldn't stop {module}: {error}", module.ModuleClassName, e.Message);
            }
            return;
        }

        var initPacket = new EventInitPacket
        {
            expressionAvailable = module.ExpressionOffered,
            eyeAvailable        = module.EyeOffered,
        };
        _logger.LogInformation("Got supported for module {module}. Expr: {expr} Eye: {eye}...",
            module.ModuleClassName, initPacket.expressionAvailable, initPacket.eyeAvailable);
        _sandboxServer.SendData(initPacket, port);
    }

    private void HandleReplyInit(ReplyInitPacket reply, int port, int moduleIndex)
    {
        var module = AvailableSandboxModules[moduleIndex];
        module.ModuleInformation.Name     = reply.ModuleInformationName;
        module.SupportsEyeTracking        = module.SupportsEyeTracking        && reply.eyeSuccess;
        module.SupportsExpressionTracking = module.SupportsExpressionTracking && reply.expressionSuccess;

        _logger.LogInformation("Got init for module {module}. Eye: {eye} Expr: {expr}...",
            module.ModuleClassName, reply.eyeSuccess, reply.expressionSuccess);

        // Only honour what the module was offered, so one that ignores its init flags can't take a part assigned elsewhere
        var eyeClaimed        = reply.eyeSuccess        && module.EyeOffered;
        var expressionClaimed = reply.expressionSuccess && module.ExpressionOffered;

        // Skip modules that failed to init anything
        if (!eyeClaimed && !expressionClaimed)
        {
            return;
        }

        // Don't let a module that only claimed one part reset the status of a part another module already has
        if (eyeClaimed)
        {
            EyeStatus = ModuleState.Active;
        }
        if (expressionClaimed)
        {
            ExpressionStatus = ModuleState.Active;
        }

        module.ModuleInformation.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not (nameof(ModuleMetadataInternal.Active)
                                       or nameof(ModuleMetadataInternal.UsingEye)
                                       or nameof(ModuleMetadataInternal.UsingExpression)))
            {
                return;
            }

            module.Status = module.ModuleInformation.Active ? ModuleState.Active : ModuleState.Idle;
            _sandboxServer.SendData(
                new EventStatusUpdatePacket
                {
                    ModuleState     = module.Status,
                    UsingEye        = module.ModuleInformation.UsingEye,
                    UsingExpression = module.ModuleInformation.UsingExpression,
                },
                port);
        };

        module.ModuleInformation.Active          = true;
        module.ModuleInformation.UsingEye        = !AvailableSandboxModules.Any(m => m.ModuleInformation.UsingEye)        && eyeClaimed;
        module.ModuleInformation.UsingExpression = !AvailableSandboxModules.Any(m => m.ModuleInformation.UsingExpression) && expressionClaimed;
        module.ModuleInformation.StaticImages    = reply.IconDataStreams;

        _sendCoordinator.RegisterModule(port);
        EnsureModuleThreadStartedSandboxed(module);

        _dispatcherService.Run(() => PublishInitializedModuleToUi(moduleIndex));
    }

    private void PublishInitializedModuleToUi(int moduleIndex)
    {
        var module = AvailableSandboxModules[moduleIndex];

        var replaced = false;
        for (var i = 0; i < LoadedModulesMetadata.Count; i++)
        {
            if (LoadedModulesMetadata[i].Name == module.ModuleInformation.Name)
            {
                LoadedModulesMetadata[i] = module.ModuleInformation;
                replaced = true;
                break;
            }
        }
        if (!replaced)
        {
            LoadedModulesMetadata.Add(module.ModuleInformation);
        }

        if (AvailableSandboxModules.Count == 0)
        {
            _logger.LogWarning("No modules loaded.");
            LoadedModulesMetadata.Clear();
            LoadedModulesMetadata.Add(new ModuleMetadataInternal { Active = false, Name = "No Modules Loaded" });
            return;
        }

        if (LoadedModulesMetadata.Count > 0
            && !LoadedModulesMetadata[0].Active
            && (LoadedModulesMetadata[0].Name == "No Modules Loaded"
                || LoadedModulesMetadata[0].Name == "Initializing Modules..."))
        {
            LoadedModulesMetadata.RemoveAt(0);
        }

        if (module.ModuleInformation.Active)
        {
            _logger.LogInformation("Tracking initialized via {module}", module.ModuleClassName);
        }
    }

    private void HandleReplyUpdate(ReplyUpdatePacket reply, int moduleIndex)
    {
        var module = AvailableSandboxModules[moduleIndex];
        if (module.Status != ModuleState.Active || !module.ModuleInformation.Active)
        {
            return;
        }

        if (module.ModuleInformation.UsingEye)
        {
            reply.UpdateGlobalEyeState();
        }
        if (module.ModuleInformation.UsingExpression)
        {
            reply.UpdateGlobalExpressionState();
        }

        // Every module sends a head pose (zeroes if it doesn't track one), so take it from a single module or they fight.
        // That's the expression module, or the eye module when nothing handles expressions.
        if (module.ModuleInformation.UsingExpression
            || (module.ModuleInformation.UsingEye && ExpressionStatus == ModuleState.Uninitialized))
        {
            reply.UpdateHeadState();
        }

        _sendCoordinator.NotifyReply(module.SandboxProcessPort);
    }

    private void InitialiseSandboxesBaseOnPaths(IEnumerable<string> paths)
    {
        foreach (var dll in paths)
        {
            try
            {
                var sandboxProcess = Process.Start(new ProcessStartInfo(
                    _sandboxProcessPath,
                    $"--port {_sandboxServer.Port} --module-path \"{dll}\" --parent-pid {Environment.ProcessId}")
                {
#if !DEBUG
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
#else
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
#endif
                });

#if DEBUG
                new Thread(() =>
                {
                    var output = sandboxProcess.StandardOutput.ReadToEnd()
                               + sandboxProcess.StandardError.ReadToEnd();
                    sandboxProcess.WaitForExit();
                    Debug.WriteLine(output);
                }).Start();
#endif

                var runtimeInfo = new ModuleRuntimeInfo
                {
                    SandboxProcessPID  = sandboxProcess.Id,
                    SandboxProcessPort = -1,
                    SandboxModulePath  = dll,
                    IsActive           = true,
                    Process            = sandboxProcess,
                    ModuleClassName    = Path.GetFileNameWithoutExtension(dll),
                    ModuleInformation  = new(),
                    EventBus           = new(),
                };
                lock (AvailableSandboxModules)
                {
                    _logger.LogDebug("Started sandbox process with dll {dllPath}", dll);
                    AvailableSandboxModules.Add(runtimeInfo);
                }
            }
            catch (Exception e)
            {
                _logger.LogWarning("{error} Failed to start sandbox process for {path}. Skipping...", e.Message, dll);
            }
        }
    }

    private void EnsureModuleThreadStartedSandboxed(ModuleRuntimeInfo module)
    {
        if (_moduleThreads.Any(existing =>
                existing.SandboxProcessPID  == module.SandboxProcessPID &&
                existing.SandboxProcessPort == module.SandboxProcessPort))
        {
            return;
        }

        var port = module.SandboxProcessPort;
        var cts = new CancellationTokenSource();
        var thread = new Thread(() =>
        {
            _logger.LogDebug("Starting thread for {module}", module.GetType().Name);
            var updatePacket = new EventUpdatePacket();
            while (!cts.IsCancellationRequested)
            {
                Thread.Sleep(10); // 100Hz
                _sandboxServer.SendData(updatePacket, port);
            }
            _logger.LogDebug("Thread for {module} ended", module.GetType().Name);
        });
        thread.Start();

        module.UpdateCancellationToken = cts;
        module.UpdateThread = thread;
        _moduleThreads.Add(module);
    }

    private void AttemptSandboxedModuleInitialize(ModuleRuntimeInfo module)
    {
        var packet = new EventInitGetSupported();

        if (module.SandboxProcessPID != -1 && module.SandboxProcessPort > 0)
        {
            _sandboxServer.SendData(packet, module.SandboxProcessPort);
        }
        else
        {
            module.EventBus.Enqueue(new QueuedPacket
            {
                packet = packet,
                destinationPort = module.SandboxProcessPort,
            });
        }
    }

    private async Task<bool> TeardownModuleSandboxed(ModuleRuntimeInfo module)
    {
        _logger.LogInformation("Tearing down {module} ", module.ModuleClassName);

        _sendCoordinator.UnregisterModule(module.SandboxProcessPort);
        _sandboxServer.SendData(new EventTeardownPacket(), module.SandboxProcessPort);

        if (module.UpdateCancellationToken != null)
        {
            await module.UpdateCancellationToken.CancelAsync();
        }

        if (!(module.Process?.HasExited ?? true))
        {
            _logger.LogDebug("Module process has not yet exited");
            try
            {
                if (!(module.Process?.WaitForExit(200) ?? false))
                {
                    _logger.LogDebug("Module {id} didn't exit gracefully. Forcing kill...", module.Process?.Id ?? -1);
                    module.Process?.Kill(entireProcessTree: true);

                    if (!(module.Process?.WaitForExit(2000) ?? false))
                    {
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        {
                            // taskkill /F /T has a higher success rate than Process.Kill for stubborn processes.
                            using var killer = Process.Start(new ProcessStartInfo
                            {
                                FileName = "taskkill",
                                Arguments = $"/F /T /PID {module.Process.Id}",
                                CreateNoWindow = true,
                                UseShellExecute = false,
                            });
                            killer?.WaitForExit(2000);
                        }
                        else
                        {
                            _logger.LogCritical("Process {id} is a zombie or stuck in Kernel I/O. Manual intervention required.", module.Process.Id);
                        }
                        return false;
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // OpenProcessEx can fail with ACCESS_DENIED (e.g. process has higher privileges).
                _logger.LogError($"Tried killing process with PID {module.Process.Id}. Got win32 error ({ex})");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Tried killing process with PID {module.Process.Id}. Got exception ({ex.HResult}) {ex.Message}");
            }
        }

        if (module.UpdateThread?.IsAlive ?? false)
        {
            var moduleName = module.ModuleInformation?.Name ?? module.ModuleClassName ?? "Unknown";
            _logger.LogDebug("Waiting for {module}'s thread to join...", moduleName);
            module.UpdateThread?.Join(500);
        }

        return true;
    }
}
