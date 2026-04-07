using System.Collections.Concurrent;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

/// <inheritdoc />
public sealed class OrchestrationExecutionService : IOrchestrationExecutionService, IHostedService
{
    private sealed record ActiveSession(
        string JobId,
        string SessionId,
        string? MachineId,
        TaskCompletionSource<int> Completion,
        bool IsVsCode = false);

    private readonly IOrchestrationJobService _jobs;
    private readonly IPtyProcessManager _ptyManager;
    private readonly IVsCodeDebugSessionService _vsCodeDebug;
    private readonly IWorkspaceService _workspace;
    private readonly ILogger<OrchestrationExecutionService> _logger;
    private readonly ConcurrentDictionary<string, byte> _activeJobs = new();
    private readonly ConcurrentDictionary<string, string> _jobSessions = new();
    private readonly ConcurrentDictionary<string, ActiveSession> _sessions = new();

    public OrchestrationExecutionService(
        IOrchestrationJobService jobs,
        IPtyProcessManager ptyManager,
        IVsCodeDebugSessionService vsCodeDebug,
        IWorkspaceService workspace,
        ILogger<OrchestrationExecutionService> logger)
    {
        _jobs = jobs;
        _ptyManager = ptyManager;
        _vsCodeDebug = vsCodeDebug;
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
            return false;
        }

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
                    await _vsCodeDebug.StopAsync(sessionId, cancellationToken);
                    return;
                }
            }

            await _ptyManager.KillAsync(sessionId, cancellationToken);
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
                return;
            }

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
            _jobs.AppendLog(jobId, new AppendOrchestrationJobLogRequest
            {
                Level = OrchestrationLogLevel.Information,
                Message = $"Starting execution on local runner in '{workingDirectory}'.",
                MachineId = job.TargetMachineId
            });

            var buildExitCode = await RunPhaseAsync(
                job,
                command: job.BuildCommand,
                status: OrchestrationJobStatus.Preparing,
                statusMessage: "Building project.",
                fallbackMachineId: job.TargetMachineId);

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
                command: launchCommand,
                status: OrchestrationJobStatus.Running,
                statusMessage: "Launching application.",
                fallbackMachineId: job.TargetMachineId);

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
        _jobs.AppendLog(job.Id, new AppendOrchestrationJobLogRequest
        {
            Level = OrchestrationLogLevel.Information,
            Message = $"Preparing VS Code debug execution in '{workingDirectory}'.",
            MachineId = job.TargetMachineId
        });

        var buildExitCode = await RunPhaseAsync(
            job,
            command: job.BuildCommand,
            status: OrchestrationJobStatus.Preparing,
            statusMessage: "Building project for VS Code debugging.",
            fallbackMachineId: job.TargetMachineId);

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

        var orchestrationSessionId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeSession = new ActiveSession(job.Id, orchestrationSessionId, job.TargetMachineId, completion, IsVsCode: true);
        _sessions[orchestrationSessionId] = activeSession;
        _jobSessions[job.Id] = orchestrationSessionId;

        try
        {
            _jobs.UpdateStatus(job.Id, new UpdateOrchestrationJobStatusRequest
            {
                Status = OrchestrationJobStatus.Dispatching,
                SessionId = orchestrationSessionId,
                Message = "Launching VS Code debug host on the local runner."
            });

            var launch = await _vsCodeDebug.LaunchAsync(orchestrationSessionId, job, workingDirectory);
            _jobs.AppendLog(job.Id, new AppendOrchestrationJobLogRequest
            {
                Level = OrchestrationLogLevel.Information,
                Message = $"VS Code opened in '{launch.WorkspaceDirectory}' for '{Path.GetFileName(launch.StartupProjectPath)}'.",
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

            _jobs.UpdateStatus(job.Id, new UpdateOrchestrationJobStatusRequest
            {
                Status = OrchestrationJobStatus.Running,
                SessionId = orchestrationSessionId,
                ViewerSessionId = launch.ViewerSessionId,
                Message = $"Debugging through VS Code configuration '{job.DebugConfigurationName ?? job.LaunchProfileName}'."
            });

            var exitCode = await _vsCodeDebug.WaitForExitAsync(orchestrationSessionId);
            await FinishIfTerminalAsync(job.Id, exitCode, "Launch");
        }
        finally
        {
            _sessions.TryRemove(orchestrationSessionId, out _);
        }
    }

    private async Task<int> RunPhaseAsync(
        OrchestrationJob job,
        string command,
        OrchestrationJobStatus status,
        string statusMessage,
        string? fallbackMachineId)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("Orchestration phase command was empty.");
        }

        var workingDirectory = ResolveWorkingDirectory(job.WorkingDirectory);
        var sessionId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeSession = new ActiveSession(job.Id, sessionId, fallbackMachineId, completion);
        _sessions[sessionId] = activeSession;
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
            MachineId = fallbackMachineId
        });

        var (shellCommand, shellArguments) = ShellLaunchBuilder.BuildBatchLaunch(command, workingDirectory);

        try
        {
            await _ptyManager.StartAsync(sessionId, shellCommand, shellArguments, workingDirectory, 120, 40);
            return await completion.Task;
        }
        finally
        {
            await _ptyManager.KillAsync(sessionId);
            _sessions.TryRemove(sessionId, out _);
        }
    }

    private async Task<bool> FinishIfTerminalAsync(string jobId, int exitCode, string phaseName)
    {
        var job = _jobs.Get(jobId);
        if (job is null)
        {
            return true;
        }

        if (job.Status is OrchestrationJobStatus.CancelRequested or OrchestrationJobStatus.Cancelled)
        {
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
            _jobs.UpdateStatus(jobId, new UpdateOrchestrationJobStatusRequest
            {
                Status = OrchestrationJobStatus.Completed,
                ExitCode = exitCode,
                Message = "Launch completed successfully."
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
    }

    private void OnProcessExited(object? sender, (string SessionId, int ExitCode) e)
    {
        if (_sessions.TryGetValue(e.SessionId, out var session))
        {
            session.Completion.TrySetResult(e.ExitCode);
        }
    }

    private string ResolveWorkingDirectory(string workingDirectory) =>
        Path.IsPathRooted(workingDirectory)
            ? workingDirectory
            : _workspace.ResolveDirectory(workingDirectory);
}
