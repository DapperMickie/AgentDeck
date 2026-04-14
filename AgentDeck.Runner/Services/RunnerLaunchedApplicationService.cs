using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using RdpPoc.Contracts;

namespace AgentDeck.Runner.Services;

public sealed class RunnerLaunchedApplicationService : IRunnerLaunchedApplicationService, IHostedService
{
    private const string CtrlC = "\u0003";

    private sealed record TrackedApplication(
        string ViewerSessionId,
        string DisplayName,
        string? TerminalSessionId,
        int? RunnerProcessId,
        int? ResolvedProcessId,
        string? TargetId,
        string? TargetDisplayName,
        DateTimeOffset UpdatedAt);

    private readonly ConcurrentDictionary<string, TrackedApplication> _trackedApplications = new(StringComparer.OrdinalIgnoreCase);
    private readonly IPtyProcessManager _ptyManager;
    private readonly ILogger<RunnerLaunchedApplicationService> _logger;

    public RunnerLaunchedApplicationService(
        IPtyProcessManager ptyManager,
        ILogger<RunnerLaunchedApplicationService> logger)
    {
        _ptyManager = ptyManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var viewerSessionIds = _trackedApplications.Keys.ToArray();
        foreach (var viewerSessionId in viewerSessionIds)
        {
            await CloseViewerApplicationAsync(
                viewerSessionId,
                "Runner shutdown closed a runner-managed application.",
                cancellationToken);
        }
    }

    public void TrackViewerSession(string viewerSessionId, string displayName, string? terminalSessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewerSessionId);

        _trackedApplications.AddOrUpdate(
            viewerSessionId.Trim(),
            _ => new TrackedApplication(
                viewerSessionId.Trim(),
                string.IsNullOrWhiteSpace(displayName) ? viewerSessionId.Trim() : displayName.Trim(),
                string.IsNullOrWhiteSpace(terminalSessionId) ? null : terminalSessionId.Trim(),
                null,
                null,
                null,
                null,
                DateTimeOffset.UtcNow),
            (_, existing) => existing with
            {
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? existing.DisplayName : displayName.Trim(),
                TerminalSessionId = string.IsNullOrWhiteSpace(terminalSessionId) ? existing.TerminalSessionId : terminalSessionId.Trim(),
                UpdatedAt = DateTimeOffset.UtcNow
            });
    }

    public void UpdateTrackedProcess(string viewerSessionId, int processId, string? targetId = null, string? targetDisplayName = null)
    {
        if (string.IsNullOrWhiteSpace(viewerSessionId) || processId <= 0)
        {
            return;
        }

        if (!_trackedApplications.TryGetValue(viewerSessionId.Trim(), out var existing))
        {
            return;
        }

        _trackedApplications[viewerSessionId.Trim()] = existing with
        {
            RunnerProcessId = processId,
            TargetId = string.IsNullOrWhiteSpace(targetId) ? existing.TargetId : targetId.Trim(),
            TargetDisplayName = string.IsNullOrWhiteSpace(targetDisplayName) ? existing.TargetDisplayName : targetDisplayName.Trim(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public void UpdateResolvedTarget(string viewerSessionId, CaptureTargetDescriptor target)
    {
        if (target.OwnerProcessId is null)
        {
            return;
        }

        if (!_trackedApplications.TryGetValue(viewerSessionId.Trim(), out var existing))
        {
            return;
        }

        _trackedApplications[viewerSessionId.Trim()] = existing with
        {
            ResolvedProcessId = target.OwnerProcessId.Value,
            TargetId = target.Id,
            TargetDisplayName = target.DisplayName,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public async Task CloseViewerApplicationAsync(string viewerSessionId, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(viewerSessionId) ||
            !_trackedApplications.TryRemove(viewerSessionId.Trim(), out var trackedApplication))
        {
            return;
        }

        if (TryCloseTrackedProcess(trackedApplication, reason))
        {
            return;
        }

        if (await TryInterruptTerminalAsync(trackedApplication, reason, cancellationToken))
        {
            return;
        }

        _logger.LogInformation(
            "Runner-managed app tracking removed viewer {ViewerSessionId} ({DisplayName}) without an active process or terminal fallback. Reason: {Reason}",
            trackedApplication.ViewerSessionId,
            trackedApplication.DisplayName,
            reason);
    }

    public void UntrackViewerSession(string viewerSessionId)
    {
        if (string.IsNullOrWhiteSpace(viewerSessionId))
        {
            return;
        }

        _trackedApplications.TryRemove(viewerSessionId.Trim(), out _);
    }

    private bool TryCloseTrackedProcess(TrackedApplication trackedApplication, string reason)
    {
        var processId = trackedApplication.RunnerProcessId ?? trackedApplication.ResolvedProcessId;
        if (processId is null)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            if (process.HasExited)
            {
                _logger.LogInformation(
                    "Runner-managed app for viewer {ViewerSessionId} ({DisplayName}) had already exited before cleanup. Reason: {Reason}",
                    trackedApplication.ViewerSessionId,
                    trackedApplication.DisplayName,
                    reason);
                return true;
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            _logger.LogInformation(
                "Closed runner-managed process {ProcessId} for viewer {ViewerSessionId} ({DisplayName}) targeting {TargetId} ({TargetDisplayName}). Reason: {Reason}",
                processId.Value,
                trackedApplication.ViewerSessionId,
                trackedApplication.DisplayName,
                trackedApplication.TargetId ?? "<unknown>",
                trackedApplication.TargetDisplayName ?? "<unknown>",
                reason);
            return true;
        }
        catch (ArgumentException)
        {
            _logger.LogInformation(
                "Runner-managed process {ProcessId} for viewer {ViewerSessionId} ({DisplayName}) was already gone during cleanup. Reason: {Reason}",
                processId.Value,
                trackedApplication.ViewerSessionId,
                trackedApplication.DisplayName,
                reason);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to close runner-managed process {ProcessId} for viewer {ViewerSessionId} ({DisplayName}). Trying terminal fallback if available.",
                processId.Value,
                trackedApplication.ViewerSessionId,
                trackedApplication.DisplayName);
            return false;
        }
    }

    private async Task<bool> TryInterruptTerminalAsync(
        TrackedApplication trackedApplication,
        string reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(trackedApplication.TerminalSessionId) ||
            !_ptyManager.IsActive(trackedApplication.TerminalSessionId))
        {
            return false;
        }

        await _ptyManager.WriteAsync(trackedApplication.TerminalSessionId, CtrlC, cancellationToken);
        _logger.LogInformation(
            "Sent Ctrl+C to terminal {TerminalSessionId} while closing runner-managed app for viewer {ViewerSessionId} ({DisplayName}). Reason: {Reason}",
            trackedApplication.TerminalSessionId,
            trackedApplication.ViewerSessionId,
            trackedApplication.DisplayName,
            reason);
        return true;
    }
}
