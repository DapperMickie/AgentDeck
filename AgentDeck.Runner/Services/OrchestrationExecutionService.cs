using System.Collections.Concurrent;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;
using RdpPoc.Contracts;

namespace AgentDeck.Runner.Services;

/// <inheritdoc />
public sealed class OrchestrationExecutionService : IOrchestrationExecutionService, IHostedService
{
    private const string CtrlC = "\u0003";
    private const int FailureOutputTailLimit = 8192;

    private sealed class ActiveSession
    {
        public required string JobId { get; init; }
        public required string SessionId { get; init; }
        public string? MachineId { get; init; }
        public required TaskCompletionSource<int> Completion { get; init; }
        public required string CompletionMarker { get; init; }
        public string? ProcessIdMarker { get; init; }
        public bool IsVsCode { get; init; }
        public string MarkerBuffer { get; set; } = string.Empty;
        public string RecentOutputTail { get; set; } = string.Empty;
        public string? CompletionOutputTail { get; set; }
    }

    private readonly IOrchestrationJobService _jobs;
    private readonly IPtyProcessManager _ptyManager;
    private readonly IAgentSessionStore _sessionStore;
    private readonly IRemoteViewerSessionService _viewers;
    private readonly IManagedViewerRelayService _managedViewerRelay;
    private readonly IDesktopViewerBootstrapService _viewerBootstrap;
    private readonly IVsCodeDebugSessionService _vsCodeDebug;
    private readonly IRunnerLaunchedApplicationService _launchedApplications;
    private readonly IWorkspaceService _workspace;
    private readonly ILogger<OrchestrationExecutionService> _logger;
    private readonly ConcurrentDictionary<string, byte> _activeJobs = new();
    private readonly ConcurrentDictionary<string, string> _jobSessions = new();
    private readonly ConcurrentDictionary<string, ActiveSession> _sessions = new();

    public OrchestrationExecutionService(
        IOrchestrationJobService jobs,
        IPtyProcessManager ptyManager,
        IAgentSessionStore sessionStore,
        IRemoteViewerSessionService viewers,
        IManagedViewerRelayService managedViewerRelay,
        IDesktopViewerBootstrapService viewerBootstrap,
        IVsCodeDebugSessionService vsCodeDebug,
        IRunnerLaunchedApplicationService launchedApplications,
        IWorkspaceService workspace,
        ILogger<OrchestrationExecutionService> logger)
    {
        _jobs = jobs;
        _ptyManager = ptyManager;
        _sessionStore = sessionStore;
        _viewers = viewers;
        _managedViewerRelay = managedViewerRelay;
        _viewerBootstrap = viewerBootstrap;
        _vsCodeDebug = vsCodeDebug;
        _launchedApplications = launchedApplications;
        _workspace = workspace;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ptyManager.OutputReceived += OnOutputReceived;
        _ptyManager.ProcessExited += OnProcessExited;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ptyManager.OutputReceived -= OnOutputReceived;
        _ptyManager.ProcessExited -= OnProcessExited;
        return Task.CompletedTask;
    }

    public bool Start(string jobId)
    {
        if (!_activeJobs.TryAdd(jobId, 0))
        {
            _logger.LogWarning("Ignored duplicate start request for orchestration job {JobId} because it is already active.", jobId);
            return false;
        }

        _logger.LogInformation("Starting orchestration execution for job {JobId}.", jobId);
        _ = Task.Run(() => ExecuteJobAsync(jobId), CancellationToken.None);
        return true;
    }

    public async Task RequestCancellationAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (_jobSessions.TryGetValue(jobId, out var sessionId))
        {
            if (_sessions.TryGetValue(sessionId, out var activeSession))
            {
                activeSession.Completion.TrySetResult(-1);

                if (activeSession.IsVsCode)
                {
                    await _vsCodeDebug.StopAsync(jobId, cancellationToken);
                }
            }

            if (_ptyManager.IsActive(sessionId))
            {
                await _ptyManager.WriteAsync(sessionId, CtrlC, cancellationToken);
            }
            return;
        }

        var job = _jobs.Get(jobId);
        if (job?.Status == OrchestrationJobStatus.CancelRequested)
        {
            _jobs.UpdateStatus(jobId, new UpdateOrchestrationJobStatusRequest
            {
                Status = OrchestrationJobStatus.Cancelled,
                Message = "Job cancelled before execution started."
            });
        }
    }

    private async Task ExecuteJobAsync(string jobId)
    {
        try
        {
            var job = _jobs.Get(jobId);
            if (job is null)
            {
                _logger.LogWarning("Orchestration job {JobId} disappeared before execution started.", jobId);
                return;
            }

            _logger.LogInformation(
                "Executing orchestration job {JobId} for project {ProjectId} ({ProjectName}); profile {LaunchProfileId}; mode {Mode}; platform {Platform}; terminal {TerminalSessionId}; working directory {WorkingDirectory}",
                job.Id,
                job.ProjectId,
                job.ProjectName,
                job.LaunchProfileId,
                job.Mode,
                job.Platform,
                job.SessionId ?? "<none>",
                job.WorkingDirectory);

            if (job.Status is OrchestrationJobStatus.CancelRequested or OrchestrationJobStatus.Cancelled)
            {
                _jobs.UpdateStatus(jobId, new UpdateOrchestrationJobStatusRequest
                {
                    Status = OrchestrationJobStatus.Cancelled,
                    Message = "Job cancelled before execution started."
                });
                return;
            }

            if (job.Mode == ProjectLaunchMode.Debug || job.LaunchDriver == ProjectLaunchDriver.VsCode)
            {
                await ExecuteVsCodeDebugAsync(job);
                return;
            }

            var workingDirectory = ResolveWorkingDirectory(job.WorkingDirectory);
            var terminalSessionId = RequireOwnedTerminalSessionId(job);
            _logger.LogInformation(
                "Orchestration job {JobId} resolved owned terminal {TerminalSessionId} at {WorkingDirectory} for build/launch execution.",
                job.Id,
                terminalSessionId,
                workingDirectory);
            _jobs.AppendLog(jobId, new AppendOrchestrationJobLogRequest
            {
                Level = OrchestrationLogLevel.Information,
                Message = $"Starting execution in owned terminal '{terminalSessionId}' at '{workingDirectory}'.",
                MachineId = job.TargetMachineId
            });

            var buildExitCode = await RunPhaseAsync(
                job,
                sessionId: terminalSessionId,
                command: job.BuildCommand,
                status: OrchestrationJobStatus.Preparing,
                statusMessage: "Building project in the owned terminal.");

            if (await FinishIfTerminalAsync(jobId, buildExitCode, "Build"))
            {
                return;
            }

            job = _jobs.Get(jobId);
            if (job is null)
            {
                return;
            }

            if (job.Status is OrchestrationJobStatus.CancelRequested or OrchestrationJobStatus.Cancelled)
            {
                _jobs.UpdateStatus(jobId, new UpdateOrchestrationJobStatusRequest
                {
                    Status = OrchestrationJobStatus.Cancelled,
                    ExitCode = buildExitCode,
                    Message = "Job cancelled."
                });
                return;
            }

            if (string.IsNullOrWhiteSpace(job.LaunchCommand))
            {
                _jobs.UpdateStatus(jobId, new UpdateOrchestrationJobStatusRequest
                {
                    Status = OrchestrationJobStatus.Completed,
                    ExitCode = 0,
                    Message = "Build completed; no launch command was configured."
                });
                return;
            }

            _jobs.UpdateStatus(jobId, new UpdateOrchestrationJobStatusRequest
            {
                Status = OrchestrationJobStatus.Dispatching,
                Message = "Dispatching to the local runner."
            });

            job = _jobs.Get(jobId);
            if (job is null)
            {
                return;
            }

            if (job.Status is OrchestrationJobStatus.CancelRequested or OrchestrationJobStatus.Cancelled)
            {
                _jobs.UpdateStatus(jobId, new UpdateOrchestrationJobStatusRequest
                {
                    Status = OrchestrationJobStatus.Cancelled,
                    Message = "Job cancelled before launch started."
                });
                return;
            }

            var launchCommand = job.LaunchCommand;
            if (string.IsNullOrWhiteSpace(launchCommand))
            {
                throw new InvalidOperationException("Launch command became unavailable before execution.");
            }

            var launchExitCode = await RunPhaseAsync(
                job,
                sessionId: terminalSessionId,
                command: launchCommand,
                status: OrchestrationJobStatus.Running,
                statusMessage: "Launching application.",
                attachRuntimeViewer: true);

            await FinishIfTerminalAsync(jobId, launchExitCode, "Launch");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute orchestration job {JobId}", jobId);
            _jobs.AppendLog(jobId, new AppendOrchestrationJobLogRequest
            {
                Level = OrchestrationLogLevel.Error,
                Message = ex.Message
            });
            _jobs.UpdateStatus(jobId, new UpdateOrchestrationJobStatusRequest
            {
                Status = OrchestrationJobStatus.Failed,
                Message = $"Execution failed: {ex.Message}"
            });
            await CloseJobViewerIfPresentAsync(jobId, $"Job failed: {ex.Message}");
        }
        finally
        {
            _jobSessions.TryRemove(jobId, out _);
            _activeJobs.TryRemove(jobId, out _);
        }
    }

    private async Task ExecuteVsCodeDebugAsync(OrchestrationJob job)
    {
        var workingDirectory = ResolveWorkingDirectory(job.WorkingDirectory);
        var terminalSessionId = RequireOwnedTerminalSessionId(job);
        _jobs.AppendLog(job.Id, new AppendOrchestrationJobLogRequest
        {
            Level = OrchestrationLogLevel.Information,
            Message = $"Preparing VS Code debug execution in owned terminal '{terminalSessionId}' at '{workingDirectory}'.",
            MachineId = job.TargetMachineId
        });

        var buildExitCode = await RunPhaseAsync(
            job,
            sessionId: terminalSessionId,
            command: job.BuildCommand,
            status: OrchestrationJobStatus.Preparing,
            statusMessage: "Building project for VS Code debugging in the owned terminal.");

        if (await FinishIfTerminalAsync(job.Id, buildExitCode, "Build"))
        {
            return;
        }

        job = _jobs.Get(job.Id) ?? job;
        if (job.Status is OrchestrationJobStatus.CancelRequested or OrchestrationJobStatus.Cancelled)
        {
            _jobs.UpdateStatus(job.Id, new UpdateOrchestrationJobStatusRequest
            {
                Status = OrchestrationJobStatus.Cancelled,
                ExitCode = buildExitCode,
                Message = "Job cancelled before VS Code debugging started."
            });
            return;
        }

        _jobs.UpdateStatus(job.Id, new UpdateOrchestrationJobStatusRequest
        {
            Status = OrchestrationJobStatus.Dispatching,
            SessionId = terminalSessionId,
            Message = "Launching VS Code from the owned terminal."
        });
        _logger.LogInformation(
            "Orchestration job {JobId} launching VS Code debug through owned terminal {TerminalSessionId}.",
            job.Id,
            terminalSessionId);

        var launch = await _vsCodeDebug.LaunchAsync(job.Id, job, workingDirectory);
        _jobs.AppendLog(job.Id, new AppendOrchestrationJobLogRequest
        {
            Level = OrchestrationLogLevel.Information,
            Message = $"VS Code launch prepared in '{launch.WorkspaceDirectory}' for '{Path.GetFileName(launch.StartupProjectPath)}'.",
            MachineId = job.TargetMachineId
        });

        if (job.DeviceSelection?.HasTarget == true)
        {
            _jobs.AppendLog(job.Id, new AppendOrchestrationJobLogRequest
            {
                Level = OrchestrationLogLevel.Information,
                Message = $"Debug launch recorded device selection '{job.DeviceSelection.DisplayName ?? job.DeviceSelection.DeviceId ?? job.DeviceSelection.ProfileId}'.",
                MachineId = job.TargetMachineId
            });
        }

        var exitCode = await RunPhaseAsync(
            job,
            sessionId: terminalSessionId,
            command: BuildVsCodeLaunchCommand(launch.LaunchCommand, launch.LaunchArguments),
            status: OrchestrationJobStatus.Running,
            statusMessage: $"Debugging through VS Code configuration '{job.DebugConfigurationName ?? job.LaunchProfileName}'.",
            isVsCode: true,
            onBeforeWaitAsync: async activeSession =>
            {
                _jobs.UpdateStatus(job.Id, new UpdateOrchestrationJobStatusRequest
                {
                    Status = OrchestrationJobStatus.Running,
                    SessionId = terminalSessionId,
                    ViewerSessionId = launch.ViewerSessionId,
                    Message = $"Debugging through VS Code configuration '{job.DebugConfigurationName ?? job.LaunchProfileName}'."
                });
                await _vsCodeDebug.NotifyHostReadyAsync(job.Id, job.ProjectName);
            });

        await _vsCodeDebug.CompleteAsync(job.Id, exitCode);
        await FinishIfTerminalAsync(job.Id, exitCode, "Launch");
    }

    private async Task<int> RunPhaseAsync(
        OrchestrationJob job,
        string sessionId,
        string command,
        OrchestrationJobStatus status,
        string statusMessage,
        bool attachRuntimeViewer = false,
        bool isVsCode = false,
        Func<ActiveSession, Task>? onBeforeWaitAsync = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("Orchestration phase command was empty.");
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException("An owned terminal session is required for orchestration execution.");
        }

        var existingSession = _sessionStore.Get(sessionId);
        if (existingSession is null || !_ptyManager.IsActive(sessionId))
        {
            throw new InvalidOperationException($"Owned terminal session '{sessionId}' is not currently active on the runner.");
        }

        _logger.LogInformation(
            "Orchestration job {JobId} entering phase {Status} on terminal {TerminalSessionId} with command: {Command}",
            job.Id,
            status,
            sessionId,
            command);

        var operationId = Guid.NewGuid().ToString("N");
        var completionMarker = $"__AGENTDECK_EXIT_{operationId}__";
        var processIdMarker = isVsCode ? $"__AGENTDECK_PID_{operationId}__" : null;
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeSession = new ActiveSession
        {
            JobId = job.Id,
            SessionId = sessionId,
            MachineId = job.TargetMachineId,
            Completion = completion,
            CompletionMarker = completionMarker,
            ProcessIdMarker = processIdMarker,
            IsVsCode = isVsCode
        };
        if (!_sessions.TryAdd(sessionId, activeSession))
        {
            throw new InvalidOperationException($"Owned terminal session '{sessionId}' is already executing another AgentDeck orchestration command.");
        }

        _jobSessions[job.Id] = sessionId;

        _jobs.UpdateStatus(job.Id, new UpdateOrchestrationJobStatusRequest
        {
            Status = status,
            Message = statusMessage,
            SessionId = sessionId
        });

        _jobs.AppendLog(job.Id, new AppendOrchestrationJobLogRequest
        {
            Level = OrchestrationLogLevel.Information,
            Message = $"> {command}",
            MachineId = job.TargetMachineId
        });

        var knownWindowTargetIds = attachRuntimeViewer
            ? CaptureKnownWindowTargetIds()
            : [];

        try
        {
            await _ptyManager.WriteAsync(sessionId, BuildTerminalPhaseInput(command, completionMarker, processIdMarker));
            if (attachRuntimeViewer)
            {
                await TryAttachRuntimeViewerAsync(job, sessionId, knownWindowTargetIds);
            }

            if (onBeforeWaitAsync is not null)
            {
                await onBeforeWaitAsync(activeSession);
            }

            var exitCode = await completion.Task;
            if (exitCode != 0)
            {
                var recentOutputTail = string.IsNullOrWhiteSpace(activeSession.CompletionOutputTail)
                    ? activeSession.RecentOutputTail
                    : activeSession.CompletionOutputTail;
                recentOutputTail = string.IsNullOrWhiteSpace(recentOutputTail)
                    ? null
                    : recentOutputTail.Trim();
                if (!string.IsNullOrWhiteSpace(recentOutputTail))
                {
                    _logger.LogWarning(
                        "Orchestration job {JobId} phase {Status} failed on terminal {TerminalSessionId} with exit code {ExitCode}. Recent output:{NewLine}{RecentOutput}",
                        job.Id,
                        status,
                        sessionId,
                        exitCode,
                        Environment.NewLine,
                        recentOutputTail);
                    _jobs.AppendLog(job.Id, new AppendOrchestrationJobLogRequest
                    {
                        Level = OrchestrationLogLevel.Error,
                        Message = $"Recent terminal output before failure:{Environment.NewLine}{recentOutputTail}",
                        MachineId = job.TargetMachineId
                    });
                }
            }

            _logger.LogInformation(
                "Orchestration job {JobId} phase {Status} finished on terminal {TerminalSessionId} with exit code {ExitCode}.",
                job.Id,
                status,
                sessionId,
                exitCode);
            return exitCode;
        }
        finally
        {
            _sessions.TryRemove(sessionId, out _);
        }
    }

    private static string RequireOwnedTerminalSessionId(OrchestrationJob job) =>
        !string.IsNullOrWhiteSpace(job.SessionId)
            ? job.SessionId
            : throw new InvalidOperationException($"Project '{job.ProjectName}' does not currently have an owned terminal session for orchestration.");

    private static string BuildVsCodeLaunchCommand(string command, IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return command;
        }

        if (OperatingSystem.IsWindows())
        {
            return $"'{command.Replace("'", "''")}' {string.Join(" ", arguments.Select(argument => $"'{argument.Replace("'", "''")}'"))}";
        }

        return $"{QuotePosix(command)} {string.Join(" ", arguments.Select(QuotePosix))}";
    }

    private static string BuildTerminalPhaseInput(string command, string completionMarker, string? processIdMarker)
    {
        if (OperatingSystem.IsWindows())
        {
            var lines = new List<string>
            {
                "$global:LASTEXITCODE = 0",
                "$agentDeckExit = 1",
                "try {"
            };

            if (!string.IsNullOrWhiteSpace(processIdMarker))
            {
                lines.Add($"    {BuildPowerShellVsCodeWrapper(command, processIdMarker)}");
            }
            else
            {
                lines.Add($"    {command}");
            }

            lines.Add("    if ($null -ne $LASTEXITCODE) { $agentDeckExit = [int]$LASTEXITCODE } elseif ($?) { $agentDeckExit = 0 } else { $agentDeckExit = 1 }");
            lines.Add("} catch {");
            lines.Add("    Write-Error $_");
            lines.Add("    $agentDeckExit = 1");
            lines.Add("}");
            lines.Add($"Write-Host '{completionMarker}:$agentDeckExit'");
            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }

        if (!string.IsNullOrWhiteSpace(processIdMarker))
        {
            return $"agentdeck_exit=1\n{{ {BuildPosixVsCodeWrapper(command, processIdMarker)}; agentdeck_exit=$?; }}\nprintf '{completionMarker}:%s\\n' \"$agentdeck_exit\"\n";
        }

        return $"agentdeck_exit=1\n{{ {command}; agentdeck_exit=$?; }}\nprintf '{completionMarker}:%s\\n' \"$agentdeck_exit\"\n";
    }

    private static string BuildPowerShellVsCodeWrapper(string command, string processIdMarker)
    {
        var parsed = SplitQuotedArguments(command);
        var executable = parsed.FirstOrDefault()
            ?? throw new InvalidOperationException("VS Code launch command was empty.");
        var arguments = parsed.Skip(1).ToArray();
        var lines = new List<string>
        {
            $"$agentDeckProc = Start-Process -FilePath '{executable.Replace("'", "''")}' -ArgumentList @({string.Join(", ", arguments.Select(argument => $"'{argument.Replace("'", "''")}'"))}) -PassThru"
        };
        lines.Add($"Write-Host '{processIdMarker}:$($agentDeckProc.Id)'");
        lines.Add("Wait-Process -Id $agentDeckProc.Id");
        lines.Add("$agentDeckProc.Refresh()");
        lines.Add("$global:LASTEXITCODE = $agentDeckProc.ExitCode");
        return string.Join("; ", lines);
    }

    private static string BuildPosixVsCodeWrapper(string command, string processIdMarker) =>
        $"{command} & agentdeck_pid=$!; printf '{processIdMarker}:%s\\n' \"$agentdeck_pid\"; wait \"$agentdeck_pid\"";

    private static IReadOnlyList<string> SplitQuotedArguments(string value)
    {
        var arguments = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quote == '\0' && char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                if (quote == character &&
                    index + 1 < value.Length &&
                    value[index + 1] == character)
                {
                    current.Append(character);
                    index++;
                    continue;
                }

                if (quote == '\0')
                {
                    quote = character;
                    continue;
                }

                if (quote == character)
                {
                    quote = '\0';
                    continue;
                }
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            arguments.Add(current.ToString());
        }

        return arguments;
    }

    private static string QuotePosix(string value) =>
        $"'{value.Replace("'", "'\"'\"'")}'";

    private async Task<bool> FinishIfTerminalAsync(string jobId, int exitCode, string phaseName)
    {
        var job = _jobs.Get(jobId);
        if (job is null)
        {
            return true;
        }

        if (job.Status is OrchestrationJobStatus.CancelRequested or OrchestrationJobStatus.Cancelled)
        {
            await CloseJobViewerIfPresentAsync(jobId, "Viewer session closed because the job was cancelled.");
            _jobs.UpdateStatus(jobId, new UpdateOrchestrationJobStatusRequest
            {
                Status = OrchestrationJobStatus.Cancelled,
                ExitCode = exitCode,
                Message = "Job cancelled."
            });
            return true;
        }

        if (exitCode != 0)
        {
            await CloseJobViewerIfPresentAsync(jobId, $"{phaseName} failed with exit code {exitCode}.");
            _jobs.UpdateStatus(jobId, new UpdateOrchestrationJobStatusRequest
            {
                Status = OrchestrationJobStatus.Failed,
                ExitCode = exitCode,
                Message = $"{phaseName} failed with exit code {exitCode}."
            });
            return true;
        }

        if (phaseName == "Launch")
        {
            var launchCompletedMessage = string.IsNullOrWhiteSpace(job.ViewerSessionId)
                ? "Launch completed successfully."
                : "Launch completed successfully. Runtime viewer remains available.";
            _jobs.UpdateStatus(jobId, new UpdateOrchestrationJobStatusRequest
            {
                Status = OrchestrationJobStatus.Completed,
                ExitCode = exitCode,
                Message = launchCompletedMessage
            });
            return true;
        }

        _jobs.AppendLog(jobId, new AppendOrchestrationJobLogRequest
        {
            Level = OrchestrationLogLevel.Information,
            Message = $"{phaseName} completed successfully.",
            MachineId = job.TargetMachineId
        });

        await Task.CompletedTask;
        return false;
    }

    private async Task TryAttachRuntimeViewerAsync(OrchestrationJob job, string sessionId, IReadOnlyList<string> knownWindowTargetIds)
    {
        if (!TryBuildRuntimeViewerRequest(job, sessionId, knownWindowTargetIds, out var request, out var targetKind))
        {
            return;
        }

        RemoteViewerSession? viewer = null;
        try
        {
            viewer = _viewers.Create(request);
            _launchedApplications.TrackViewerSession(viewer.Id, viewer.Target.DisplayName, sessionId);
            _jobs.UpdateStatus(job.Id, new UpdateOrchestrationJobStatusRequest
            {
                Status = OrchestrationJobStatus.Running,
                SessionId = sessionId,
                ViewerSessionId = viewer.Id
            });

            viewer = await _viewerBootstrap.BootstrapAsync(viewer.Id) ?? viewer;

            var targetLabel = targetKind switch
            {
                RemoteViewerTargetKind.Emulator => "Emulator",
                RemoteViewerTargetKind.Simulator => "Simulator",
                RemoteViewerTargetKind.Window => "Window",
                _ => "Viewer"
            };
            var viewerMessage = viewer.Status switch
            {
                RemoteViewerSessionStatus.Ready => $"{targetLabel} viewer session '{viewer.Id}' is ready.",
                RemoteViewerSessionStatus.Failed => $"{targetLabel} viewer session '{viewer.Id}' failed: {viewer.StatusMessage}",
                _ => $"{targetLabel} viewer session '{viewer.Id}' is {viewer.Status}."
            };

            _jobs.AppendLog(job.Id, new AppendOrchestrationJobLogRequest
            {
                Level = viewer.Status == RemoteViewerSessionStatus.Failed ? OrchestrationLogLevel.Warning : OrchestrationLogLevel.Information,
                Message = viewerMessage,
                MachineId = job.TargetMachineId
            });
        }
        catch
        {
            if (viewer is not null)
            {
                    await _viewerBootstrap.CloseAsync(viewer.Id, "Viewer session closed because runtime viewer attachment failed.");
                }

                throw;
            }
    }

    private bool TryBuildRuntimeViewerRequest(
        OrchestrationJob job,
        string sessionId,
        IReadOnlyList<string> knownWindowTargetIds,
        out CreateRemoteViewerSessionRequest request,
        out RemoteViewerTargetKind targetKind)
    {
        request = new CreateRemoteViewerSessionRequest();
        targetKind = default;

        if (job.DeviceSelection?.HasTarget == true &&
            TryMapDeviceViewerTargetKind(job.Platform, out targetKind))
        {
            var deviceLabel = job.DeviceSelection.DisplayName ?? job.DeviceSelection.DeviceId ?? job.DeviceSelection.ProfileId ?? job.LaunchProfileName;
            request = new CreateRemoteViewerSessionRequest
            {
                MachineId = job.TargetMachineId,
                MachineName = job.TargetMachineName,
                JobId = job.Id,
                Provider = RemoteViewerProviderKind.Managed,
                Target = new RemoteViewerTarget
                {
                    Kind = targetKind,
                    DisplayName = $"{job.ProjectName} {deviceLabel}",
                    JobId = job.Id,
                    SessionId = sessionId,
                    VirtualDeviceId = job.DeviceSelection.DeviceId,
                    VirtualDeviceProfileId = job.DeviceSelection.ProfileId,
                    KnownWindowTargetIds = knownWindowTargetIds.ToArray()
                }
            };

            return true;
        }

        if (!TryMapWindowViewerTargetKind(job.Platform, out targetKind))
        {
            return false;
        }

        request = new CreateRemoteViewerSessionRequest
        {
            MachineId = job.TargetMachineId,
            MachineName = job.TargetMachineName,
            JobId = job.Id,
            Provider = RemoteViewerProviderKind.Managed,
            Target = new RemoteViewerTarget
            {
                Kind = targetKind,
                DisplayName = $"{job.ProjectName} {job.LaunchProfileName}",
                JobId = job.Id,
                SessionId = sessionId,
                WindowTitle = job.ProjectName,
                KnownWindowTargetIds = knownWindowTargetIds.ToArray()
            }
        };

        return true;
    }

    private async Task CloseJobViewerIfPresentAsync(string jobId, string message)
    {
        var viewerSessionId = _jobs.Get(jobId)?.ViewerSessionId;
        if (string.IsNullOrWhiteSpace(viewerSessionId))
        {
            return;
        }

        await _viewerBootstrap.CloseAsync(viewerSessionId, message);
    }

    private static bool TryMapDeviceViewerTargetKind(ApplicationTargetPlatform platform, out RemoteViewerTargetKind targetKind)
    {
        switch (platform)
        {
            case ApplicationTargetPlatform.Android:
                targetKind = RemoteViewerTargetKind.Emulator;
                return true;
            case ApplicationTargetPlatform.iOS:
                targetKind = RemoteViewerTargetKind.Simulator;
                return true;
            default:
                targetKind = default;
                return false;
        }
    }

    private static bool TryMapWindowViewerTargetKind(ApplicationTargetPlatform platform, out RemoteViewerTargetKind targetKind)
    {
        switch (platform)
        {
            case ApplicationTargetPlatform.Windows:
            case ApplicationTargetPlatform.Linux:
            case ApplicationTargetPlatform.MacOS:
                targetKind = RemoteViewerTargetKind.Window;
                return true;
            default:
                targetKind = default;
                return false;
        }
    }

    private IReadOnlyList<string> CaptureKnownWindowTargetIds()
    {
        try
        {
            return _managedViewerRelay.GetCaptureTargets()
                .Where(target => target.Kind == CaptureTargetKind.Window)
                .Select(target => target.Id)
                .ToArray();
        }
        catch (PlatformNotSupportedException)
        {
            return [];
        }
    }

    private void OnOutputReceived(object? sender, (string SessionId, string Data) e)
    {
        if (!_sessions.TryGetValue(e.SessionId, out var session))
        {
            return;
        }

        _jobs.AppendLog(session.JobId, new AppendOrchestrationJobLogRequest
        {
            Level = OrchestrationLogLevel.Information,
            Message = e.Data,
            MachineId = session.MachineId
        });
        session.RecentOutputTail = TruncateRecentOutput(session.RecentOutputTail + e.Data);

        var markerBuffer = session.MarkerBuffer + e.Data;
        if (!string.IsNullOrWhiteSpace(session.ProcessIdMarker) &&
            TryReadMarkedInt(ref markerBuffer, session.ProcessIdMarker, out var processId))
        {
            _ = NotifyHostStartedAsync(session.JobId, processId);
        }

        if (TryReadMarkedInt(ref markerBuffer, session.CompletionMarker, out var exitCode))
        {
            session.CompletionOutputTail = session.RecentOutputTail;
            _logger.LogInformation(
                "Detected orchestration completion marker for job {JobId} on terminal {TerminalSessionId} with exit code {ExitCode}.",
                session.JobId,
                e.SessionId,
                exitCode);
            session.Completion.TrySetResult(exitCode);
        }

        session.MarkerBuffer = TrimMarkerBuffer(markerBuffer);
    }

    private void OnProcessExited(object? sender, (string SessionId, int ExitCode) e)
    {
        if (_sessions.TryGetValue(e.SessionId, out var session))
        {
            session.CompletionOutputTail = session.RecentOutputTail;
            _logger.LogInformation(
                "Terminal {TerminalSessionId} exited while orchestration job {JobId} was active; using process exit code {ExitCode}.",
                e.SessionId,
                session.JobId,
                e.ExitCode);
            session.Completion.TrySetResult(e.ExitCode);
        }
    }

    private string ResolveWorkingDirectory(string workingDirectory) =>
        Path.IsPathRooted(workingDirectory)
            ? workingDirectory
            : _workspace.ResolveDirectory(workingDirectory);

    private static bool TryReadMarkedInt(ref string buffer, string marker, out int value)
    {
        value = 0;
        var markerIndex = buffer.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return false;
        }

        var valueStart = markerIndex + marker.Length + 1;
        if (valueStart > buffer.Length)
        {
            return false;
        }

        var lineEnd = buffer.IndexOfAny(['\r', '\n'], valueStart);
        if (lineEnd < 0)
        {
            return false;
        }

        var rawValue = buffer[valueStart..lineEnd].Trim();
        buffer = buffer[(lineEnd + 1)..];
        return int.TryParse(rawValue, out value);
    }

    private static string TrimMarkerBuffer(string buffer) =>
        buffer.Length <= 4096
            ? buffer
            : buffer.LastIndexOf("__AGENTDECK_", StringComparison.Ordinal) is var markerIndex && markerIndex >= 0 && buffer.Length - markerIndex <= 512
                ? buffer[markerIndex..]
                : buffer[^4096..];

    private static string TruncateRecentOutput(string value) =>
        value.Length <= FailureOutputTailLimit
            ? value
            : value[^FailureOutputTailLimit..];

    private async Task NotifyHostStartedAsync(string jobId, int processId)
    {
        try
        {
            await _vsCodeDebug.NotifyHostStartedAsync(jobId, processId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to record VS Code host process {ProcessId} for job {JobId}", processId, jobId);
        }
    }
}
