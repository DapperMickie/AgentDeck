using System.Collections.Concurrent;
using System.IO;
using AgentDeck.Coordinator.Hubs;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Hubs;
using AgentDeck.Shared.Models;
using Microsoft.AspNetCore.SignalR;

namespace AgentDeck.Coordinator.Services;

public sealed class RunnerBrokerService : IRunnerBrokerService
{
    private static readonly TimeSpan RunnerReconnectWaitWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RunnerReconnectPollInterval = TimeSpan.FromMilliseconds(200);

    private sealed class RunnerEntry
    {
        public required string MachineId { get; init; }
        public required SemaphoreSlim Gate { get; init; }
        public RegisteredRunnerMachine? Machine { get; set; }
        public string? ConnectionId { get; set; }
    }

    private readonly ConcurrentDictionary<string, RunnerEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _sessionMachineMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _jobMachineMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, OrchestrationJob> _orchestrationJobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _viewerMachineMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _connectionMachineMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RemoteViewerSession> _viewerSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RemoteViewerRelayFrame> _viewerFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly ICompanionRegistryService _companions;
    private readonly IWorkerRegistryService _registry;
    private readonly IHubContext<CoordinatorAgentHub, IAgentHubClient> _agentHubContext;
    private readonly IHubContext<CoordinatorViewerHub, IViewerHubClient> _viewerHubContext;
    private readonly IHubContext<CoordinatorRunnerHub, IRunnerControlClient> _runnerHubContext;
    private readonly ILogger<RunnerBrokerService> _logger;

    public RunnerBrokerService(
        ICompanionRegistryService companions,
        IWorkerRegistryService registry,
        IHubContext<CoordinatorAgentHub, IAgentHubClient> agentHubContext,
        IHubContext<CoordinatorViewerHub, IViewerHubClient> viewerHubContext,
        IHubContext<CoordinatorRunnerHub, IRunnerControlClient> runnerHubContext,
        ILogger<RunnerBrokerService> logger)
    {
        _companions = companions;
        _registry = registry;
        _agentHubContext = agentHubContext;
        _viewerHubContext = viewerHubContext;
        _runnerHubContext = runnerHubContext;
        _logger = logger;
    }

    public void AttachRunnerConnection(string machineId, string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        var entry = _entries.GetOrAdd(machineId.Trim(), static id => new RunnerEntry
        {
            MachineId = id,
            Gate = new SemaphoreSlim(1, 1)
        });

        if (!string.IsNullOrWhiteSpace(entry.ConnectionId) &&
            !string.Equals(entry.ConnectionId, connectionId, StringComparison.OrdinalIgnoreCase))
        {
            _connectionMachineMap.TryRemove(entry.ConnectionId, out _);
        }

        entry.ConnectionId = connectionId.Trim();
        _connectionMachineMap[entry.ConnectionId] = entry.MachineId;
        _logger.LogInformation("Attached coordinator runner connection {ConnectionId} to machine {MachineId}", entry.ConnectionId, entry.MachineId);
    }

    public void DetachRunnerConnection(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId) ||
            !_connectionMachineMap.TryRemove(connectionId.Trim(), out var machineId))
        {
            return;
        }

        if (_entries.TryGetValue(machineId, out var entry) &&
            string.Equals(entry.ConnectionId, connectionId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            entry.ConnectionId = null;
        }

        _logger.LogInformation("Detached coordinator runner connection {ConnectionId} from machine {MachineId}", connectionId, machineId);
    }

    public async Task PublishTerminalOutputAsync(string machineId, TerminalOutput output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        _logger.LogDebug("Runner {MachineId} published terminal output for session {SessionId}", machineId, output.SessionId);
        await _agentHubContext.Clients.Group(output.SessionId).ReceiveOutputAsync(output);
    }

    public async Task PublishTerminalSessionUpdatedAsync(string machineId, TerminalSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var entry = await EnsureEntryAsync(machineId, cancellationToken);
        var annotated = AnnotateSession(session, entry.Machine!);
        _logger.LogDebug("Runner {MachineId} published terminal session update for {SessionId}", machineId, session.Id);
        await _agentHubContext.Clients.All.SessionUpdatedAsync(annotated);
    }

    public async Task PublishTerminalSessionClosedAsync(string machineId, string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _sessionMachineMap.TryRemove(sessionId, out _);
        _companions.RemoveSessionFromAll(sessionId);
        _logger.LogInformation("Runner {MachineId} published terminal session close for {SessionId}", machineId, sessionId);
        await _agentHubContext.Clients.All.SessionClosedAsync(sessionId.Trim());
    }

    public Task PublishOrchestrationJobUpdatedAsync(string machineId, OrchestrationJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        CacheOrchestrationJob(machineId, job);
        _logger.LogDebug("Runner {MachineId} published orchestration job update for {JobId}", machineId, job.Id);
        return Task.CompletedTask;
    }

    public Task PublishViewerSessionUpdatedAsync(string machineId, RemoteViewerSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var sanitized = AnnotateViewerSession(machineId, session);
        if (sanitized.Status is RemoteViewerSessionStatus.Closed or RemoteViewerSessionStatus.Failed)
        {
            _viewerSessions.TryRemove(sanitized.Id, out _);
            _viewerFrames.TryRemove(sanitized.Id, out _);
            _viewerMachineMap.TryRemove(sanitized.Id, out _);
        }
        else
        {
            _viewerSessions[sanitized.Id] = sanitized;
            _viewerMachineMap[sanitized.Id] = machineId.Trim();
        }

        _logger.LogDebug("Runner {MachineId} published viewer session update for {ViewerSessionId}", machineId, session.Id);
        return _viewerHubContext.Clients.Group(CoordinatorViewerHub.GetViewerGroupName(sanitized.Id))
            .ViewerSessionUpdatedAsync(sanitized);
    }

    public Task PublishViewerFrameAsync(string machineId, RemoteViewerRelayFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _viewerFrames[frame.SessionId] = frame;
        _viewerMachineMap[frame.SessionId] = machineId.Trim();
        return _viewerHubContext.Clients.Group(CoordinatorViewerHub.GetViewerGroupName(frame.SessionId))
            .ViewerFramePublishedAsync(frame);
    }

    public RemoteViewerSession? GetCachedViewerSession(string viewerSessionId) =>
        string.IsNullOrWhiteSpace(viewerSessionId) || !_viewerSessions.TryGetValue(viewerSessionId.Trim(), out var session)
            ? null
            : session;

    public RemoteViewerRelayFrame? GetLatestViewerFrame(string viewerSessionId) =>
        string.IsNullOrWhiteSpace(viewerSessionId) || !_viewerFrames.TryGetValue(viewerSessionId.Trim(), out var frame)
            ? null
            : frame;

    public async Task<TerminalSession> CreateSessionAsync(string machineId, CreateTerminalRequest request, CancellationToken cancellationToken = default)
    {
        var entry = await EnsureEntryAsync(machineId, cancellationToken);
        var requestWithSessionId = string.IsNullOrWhiteSpace(request.RequestedSessionId)
            ? new CreateTerminalRequest
            {
                RequestedSessionId = Guid.NewGuid().ToString("N"),
                Name = request.Name,
                WorkingDirectory = request.WorkingDirectory,
                Command = request.Command,
                Arguments = request.Arguments,
                Cols = request.Cols,
                Rows = request.Rows
            }
            : request;
        _logger.LogInformation(
            "Brokering terminal session creation '{SessionName}' on machine {MachineName} ({MachineId}) with requested session id {RequestedSessionId}",
            request.Name,
            entry.Machine?.MachineName ?? machineId,
            machineId,
            requestWithSessionId.RequestedSessionId ?? "<none>");
        var session = await InvokeRunnerAsync(entry, "create terminal session", client => client.CreateSessionAsync(requestWithSessionId), retryOnReconnect: true, cancellationToken);
        var annotated = AnnotateSession(session, entry.Machine!);
        await _agentHubContext.Clients.All.SessionCreatedAsync(annotated);
        return annotated;
    }

    public async Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var entry = await ResolveEntryBySessionAsync(sessionId, cancellationToken);
        _logger.LogInformation("Brokering terminal session close {SessionId} on machine {MachineName} ({MachineId})", sessionId, entry.Machine?.MachineName ?? entry.MachineId, entry.MachineId);
        await InvokeRunnerAsync(entry, "close terminal session", client => client.CloseSessionAsync(sessionId), retryOnReconnect: false, cancellationToken);
        _sessionMachineMap.TryRemove(sessionId, out _);
        _companions.RemoveSessionFromAll(sessionId);
        await _agentHubContext.Clients.All.SessionClosedAsync(sessionId);
    }

    public async Task<IReadOnlyList<TerminalSession>> GetSessionsAsync(string machineId, CancellationToken cancellationToken = default)
    {
        var entry = await EnsureEntryAsync(machineId, cancellationToken);
        var sessions = await InvokeRunnerAsync(entry, "list terminal sessions", client => client.GetSessionsAsync(), retryOnReconnect: true, cancellationToken);
        return sessions.Select(session => AnnotateSession(session, entry.Machine!)).ToArray();
    }

    public Task JoinSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task LeaveSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task ResizeTerminalAsync(string sessionId, int cols, int rows, CancellationToken cancellationToken = default)
    {
        var entry = await ResolveEntryBySessionAsync(sessionId, cancellationToken);
        await InvokeRunnerAsync(entry, "resize terminal session", client => client.ResizeTerminalAsync(sessionId, cols, rows), retryOnReconnect: true, cancellationToken);
    }

    public async Task SendInputAsync(string sessionId, string data, CancellationToken cancellationToken = default)
    {
        var entry = await ResolveEntryBySessionAsync(sessionId, cancellationToken);
        await InvokeRunnerAsync(entry, "send terminal input", client => client.SendInputAsync(sessionId, data), retryOnReconnect: true, cancellationToken);
    }

    public async Task<WorkspaceInfo?> GetWorkspaceAsync(string machineId, CancellationToken cancellationToken = default)
    {
        var entry = await EnsureEntryAsync(machineId, cancellationToken);
        return await InvokeRunnerAsync(entry, "read workspace info", client => client.GetWorkspaceAsync(), retryOnReconnect: true, cancellationToken);
    }

    public async Task<OpenProjectOnRunnerResult?> OpenProjectAsync(string machineId, OpenProjectOnRunnerRequest request, string actorId, CancellationToken cancellationToken = default)
    {
        var entry = await EnsureEntryAsync(machineId, cancellationToken);
        _logger.LogInformation(
            "Brokering project open for project {ProjectId} ({ProjectName}) on machine {MachineName} ({MachineId}); existing workspace: {ExistingWorkspacePath}; repository: {RepositoryUrl}",
            request.ProjectId,
            request.ProjectName,
            entry.Machine?.MachineName ?? machineId,
            machineId,
            request.ExistingWorkspacePath ?? "<none>",
            string.IsNullOrWhiteSpace(request.Repository.Url) ? "<none>" : request.Repository.Url);
        var result = await InvokeRunnerAsync(entry, "open project", client => client.OpenProjectAsync(request, NormalizeActorId(actorId)), retryOnReconnect: false, cancellationToken);
        _logger.LogInformation(
            "Runner responded to project open for {ProjectId} on machine {MachineId} with path {ProjectPath}; terminal working directory {TerminalWorkingDirectory}; bootstrap pending: {BootstrapPending}; created: {WorkspaceCreated}; cloned: {RepositoryCloned}",
            request.ProjectId,
            machineId,
            result?.ProjectPath ?? "<none>",
            result?.TerminalWorkingDirectory ?? "<none>",
            result?.BootstrapPending,
            result?.WorkspaceCreated,
            result?.RepositoryCloned);
        return result;
    }

    public async Task<MachineCapabilitiesSnapshot> GetMachineCapabilitiesAsync(string machineId, CancellationToken cancellationToken = default)
    {
        var entry = await EnsureEntryAsync(machineId, cancellationToken);
        return await InvokeRunnerAsync(entry, "read machine capabilities", client => client.GetMachineCapabilitiesAsync(), retryOnReconnect: true, cancellationToken);
    }

    public async Task<MachineCapabilityInstallResult?> InstallMachineCapabilityAsync(string machineId, string capabilityId, MachineCapabilityInstallRequest request, string actorId, CancellationToken cancellationToken = default)
    {
        var entry = await EnsureEntryAsync(machineId, cancellationToken);
        return await InvokeRunnerAsync(entry, "install machine capability", client => client.InstallMachineCapabilityAsync(capabilityId, request, NormalizeActorId(actorId)), retryOnReconnect: false, cancellationToken);
    }

    public async Task<MachineCapabilityInstallResult?> UpdateMachineCapabilityAsync(string machineId, string capabilityId, string actorId, CancellationToken cancellationToken = default)
    {
        var entry = await EnsureEntryAsync(machineId, cancellationToken);
        return await InvokeRunnerAsync(entry, "update machine capability", client => client.UpdateMachineCapabilityAsync(capabilityId, NormalizeActorId(actorId)), retryOnReconnect: false, cancellationToken);
    }

    public async Task<bool> RetryMachineWorkflowPackAsync(string machineId, string actorId, CancellationToken cancellationToken = default)
    {
        var entry = await EnsureEntryAsync(machineId, cancellationToken);
        await InvokeRunnerAsync(entry, "retry workflow pack", client => client.RetryMachineWorkflowPackAsync(NormalizeActorId(actorId)), retryOnReconnect: false, cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<OrchestrationJob>> GetOrchestrationJobsAsync(string machineId, CancellationToken cancellationToken = default)
    {
        var entry = await TryEnsureEntryAsync(machineId, cancellationToken);
        if (entry is null)
        {
            return GetCachedOrchestrationJobs(machineId);
        }

        try
        {
            var jobs = await InvokeRunnerAsync(entry, "list orchestration jobs", client => client.GetOrchestrationJobsAsync(), retryOnReconnect: true, cancellationToken);
            ReplaceOrchestrationJobs(entry.MachineId, jobs);
            return jobs;
        }
        catch (InvalidOperationException)
        {
            return GetCachedOrchestrationJobs(machineId);
        }
    }

    public async Task<OrchestrationJob?> QueueOrchestrationJobAsync(string machineId, CreateOrchestrationJobRequest request, string actorId, CancellationToken cancellationToken = default)
    {
        var entry = await EnsureEntryAsync(machineId, cancellationToken);
        var job = await InvokeRunnerAsync(entry, "queue orchestration job", client => client.QueueOrchestrationJobAsync(request, NormalizeActorId(actorId)), retryOnReconnect: false, cancellationToken);
        if (job is not null)
        {
            CacheOrchestrationJob(entry.MachineId, job);
        }

        return job;
    }

    public async Task<OrchestrationJob?> CancelOrchestrationJobAsync(string machineId, string jobId, string actorId, CancellationToken cancellationToken = default)
    {
        var entry = await EnsureEntryAsync(machineId, cancellationToken);
        var job = await InvokeRunnerAsync(entry, "cancel orchestration job", client => client.CancelOrchestrationJobAsync(jobId, NormalizeActorId(actorId)), retryOnReconnect: false, cancellationToken);
        if (job is not null)
        {
            CacheOrchestrationJob(entry.MachineId, job);
        }

        return job;
    }

    public async Task<IReadOnlyList<RemoteViewerSession>> GetViewerSessionsAsync(string machineId, CancellationToken cancellationToken = default)
    {
        var entry = await TryEnsureEntryAsync(machineId, cancellationToken);
        if (entry is null)
        {
            return GetCachedViewerSessions(machineId);
        }

        try
        {
            var sessions = await InvokeRunnerAsync(entry, "list viewer sessions", client => client.GetViewerSessionsAsync(), retryOnReconnect: true, cancellationToken);
            var annotated = sessions.Select(session => AnnotateViewerSession(entry.MachineId, session)).ToArray();
            ReplaceViewerSessions(entry.MachineId, annotated);
            return annotated;
        }
        catch (InvalidOperationException)
        {
            return GetCachedViewerSessions(machineId);
        }
    }

    public async Task<RemoteViewerSession> CreateViewerSessionAsync(string machineId, CreateRemoteViewerSessionRequest request, string actorId, CancellationToken cancellationToken = default)
    {
        var entry = await EnsureEntryAsync(machineId, cancellationToken);
        var brokeredRequest = EnsureManagedViewerRequest(request);
        var session = await InvokeRunnerAsync(entry, "create viewer session", client => client.CreateViewerSessionAsync(brokeredRequest, NormalizeActorId(actorId)), retryOnReconnect: false, cancellationToken);
        var annotated = AnnotateViewerSession(entry.MachineId, session);
        CacheViewerSession(entry.MachineId, annotated);
        return annotated;
    }

    public async Task<RemoteViewerSession?> CloseViewerSessionAsync(string machineId, string viewerSessionId, string actorId, CancellationToken cancellationToken = default)
    {
        var entry = await EnsureEntryAsync(machineId, cancellationToken);
        var session = await InvokeRunnerAsync(entry, "close viewer session", client => client.CloseViewerSessionAsync(viewerSessionId, NormalizeActorId(actorId)), retryOnReconnect: false, cancellationToken);
        if (session is null)
        {
            return null;
        }

        var annotated = AnnotateViewerSession(entry.MachineId, session);
        if (annotated.Status is RemoteViewerSessionStatus.Closed or RemoteViewerSessionStatus.Failed)
        {
            _viewerSessions.TryRemove(annotated.Id, out _);
            _viewerFrames.TryRemove(annotated.Id, out _);
            _viewerMachineMap.TryRemove(annotated.Id, out _);
        }
        else
        {
            _viewerMachineMap[annotated.Id] = entry.MachineId;
            _viewerSessions[annotated.Id] = annotated;
        }

        await _viewerHubContext.Clients.Group(CoordinatorViewerHub.GetViewerGroupName(annotated.Id))
            .ViewerSessionUpdatedAsync(annotated);
        return annotated;
    }

    public async Task SendViewerPointerInputAsync(string machineId, string viewerSessionId, string actorId, RemoteViewerPointerInputEvent input, CancellationToken cancellationToken = default)
    {
        var entry = await EnsureEntryAsync(machineId, cancellationToken);
        await InvokeRunnerAsync(entry, "send viewer pointer input", client => client.SendViewerPointerInputAsync(viewerSessionId, NormalizeActorId(actorId), input), retryOnReconnect: false, cancellationToken);
    }

    public async Task SendViewerKeyboardInputAsync(string machineId, string viewerSessionId, string actorId, RemoteViewerKeyboardInputEvent input, CancellationToken cancellationToken = default)
    {
        var entry = await EnsureEntryAsync(machineId, cancellationToken);
        await InvokeRunnerAsync(entry, "send viewer keyboard input", client => client.SendViewerKeyboardInputAsync(viewerSessionId, NormalizeActorId(actorId), input), retryOnReconnect: false, cancellationToken);
    }

    public async Task<IReadOnlyList<VirtualDeviceCatalogSnapshot>> GetVirtualDeviceCatalogsAsync(string machineId, CancellationToken cancellationToken = default)
    {
        var entry = await EnsureEntryAsync(machineId, cancellationToken);
        return await InvokeRunnerAsync(entry, "list virtual device catalogs", client => client.GetVirtualDeviceCatalogsAsync(), retryOnReconnect: true, cancellationToken);
    }

    public async Task<VirtualDeviceLaunchResolution?> ResolveVirtualDeviceAsync(string machineId, VirtualDeviceLaunchSelection selection, CancellationToken cancellationToken = default)
    {
        var entry = await EnsureEntryAsync(machineId, cancellationToken);
        return await InvokeRunnerAsync(entry, "resolve virtual device", client => client.ResolveVirtualDeviceAsync(selection), retryOnReconnect: true, cancellationToken);
    }

    private async Task<RunnerEntry?> TryEnsureEntryAsync(string machineId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);

        var machine = await _registry.GetMachineAsync(machineId, cancellationToken)
            ?? throw new InvalidOperationException($"Coordinator does not know runner machine '{machineId}'.");

        var entry = _entries.GetOrAdd(machine.MachineId, static id => new RunnerEntry
        {
            MachineId = id,
            Gate = new SemaphoreSlim(1, 1)
        });

        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            entry.Machine = machine;
            return string.IsNullOrWhiteSpace(entry.ConnectionId) ? null : entry;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private async Task<RunnerEntry> EnsureEntryAsync(string machineId, CancellationToken cancellationToken)
    {
        var entry = await TryEnsureEntryAsync(machineId, cancellationToken);
        if (entry is not null)
        {
            return entry;
        }

        var machine = await _registry.GetMachineAsync(machineId, cancellationToken)
            ?? throw new InvalidOperationException($"Coordinator does not know runner machine '{machineId}'.");
        throw new InvalidOperationException($"Runner machine '{machine.MachineName}' does not currently have an active coordinator control connection.");
    }

    private IRunnerControlClient GetRunnerClient(RunnerEntry entry) =>
        _runnerHubContext.Clients.Client(entry.ConnectionId
            ?? throw new InvalidOperationException($"Runner machine '{entry.Machine?.MachineName ?? entry.MachineId}' does not currently have an active coordinator control connection."));

    private TerminalSession AnnotateSession(TerminalSession session, RegisteredRunnerMachine machine)
    {
        _sessionMachineMap[session.Id] = machine.MachineId;
        return new TerminalSession
        {
            Id = session.Id,
            Name = session.Name,
            WorkingDirectory = session.WorkingDirectory,
            Command = session.Command,
            Arguments = session.Arguments,
            Status = session.Status,
            ExitCode = session.ExitCode,
            CreatedAt = session.CreatedAt,
            MachineId = machine.MachineId,
            MachineName = machine.MachineName
        };
    }

    private RemoteViewerSession AnnotateViewerSession(string machineId, RemoteViewerSession session)
    {
        _viewerMachineMap[session.Id] = machineId;
        return new RemoteViewerSession(session)
        {
            MachineId = machineId,
            AccessToken = null,
            ConnectionUri = null
        };
    }

    private static CreateRemoteViewerSessionRequest EnsureManagedViewerRequest(CreateRemoteViewerSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new CreateRemoteViewerSessionRequest
        {
            MachineId = request.MachineId,
            MachineName = request.MachineName,
            JobId = request.JobId,
            Provider = request.Provider == RemoteViewerProviderKind.Auto &&
                       request.Target.Kind is not RemoteViewerTargetKind.Desktop
                ? RemoteViewerProviderKind.Managed
                : request.Provider,
            Target = new RemoteViewerTarget
            {
                Kind = request.Target.Kind,
                DisplayName = request.Target.DisplayName,
                JobId = request.Target.JobId,
                SessionId = request.Target.SessionId,
                WindowTitle = request.Target.WindowTitle,
                VirtualDeviceId = request.Target.VirtualDeviceId,
                VirtualDeviceProfileId = request.Target.VirtualDeviceProfileId
            }
        };
    }

    private void ReplaceOrchestrationJobs(string machineId, IReadOnlyList<OrchestrationJob> jobs)
    {
        var normalizedMachineId = machineId.Trim();
        var incoming = jobs.Select(job => job.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in _jobMachineMap.Where(pair => string.Equals(pair.Value, normalizedMachineId, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            if (!incoming.Contains(existing.Key))
            {
                _jobMachineMap.TryRemove(existing.Key, out _);
                _orchestrationJobs.TryRemove(existing.Key, out _);
            }
        }

        foreach (var job in jobs)
        {
            CacheOrchestrationJob(normalizedMachineId, job);
        }
    }

    private void CacheOrchestrationJob(string machineId, OrchestrationJob job)
    {
        var normalizedMachineId = machineId.Trim();
        if (_orchestrationJobs.TryGetValue(job.Id, out var existingJob) &&
            existingJob.UpdatedAt > job.UpdatedAt)
        {
            return;
        }

        _jobMachineMap[job.Id] = normalizedMachineId;
        _orchestrationJobs[job.Id] = CloneOrchestrationJob(job);
    }

    private IReadOnlyList<OrchestrationJob> GetCachedOrchestrationJobs(string machineId) =>
        _jobMachineMap
            .Where(pair => string.Equals(pair.Value, machineId.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(pair => _orchestrationJobs.TryGetValue(pair.Key, out var job) ? CloneOrchestrationJob(job) : null)
            .Where(job => job is not null)
            .Cast<OrchestrationJob>()
            .OrderByDescending(job => job.UpdatedAt)
            .ThenByDescending(job => job.CreatedAt)
            .ToArray();

    private void ReplaceViewerSessions(string machineId, IReadOnlyList<RemoteViewerSession> sessions)
    {
        var normalizedMachineId = machineId.Trim();
        var incoming = sessions.Select(session => session.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in _viewerMachineMap.Where(pair => string.Equals(pair.Value, normalizedMachineId, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            if (!incoming.Contains(existing.Key))
            {
                _viewerMachineMap.TryRemove(existing.Key, out _);
                _viewerSessions.TryRemove(existing.Key, out _);
                _viewerFrames.TryRemove(existing.Key, out _);
            }
        }

        foreach (var session in sessions)
        {
            CacheViewerSession(normalizedMachineId, session);
        }
    }

    private void CacheViewerSession(string machineId, RemoteViewerSession session)
    {
        var normalizedMachineId = machineId.Trim();
        _viewerMachineMap[session.Id] = normalizedMachineId;
        _viewerSessions[session.Id] = new RemoteViewerSession(session);
    }

    private IReadOnlyList<RemoteViewerSession> GetCachedViewerSessions(string machineId) =>
        _viewerMachineMap
            .Where(pair => string.Equals(pair.Value, machineId.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(pair => _viewerSessions.TryGetValue(pair.Key, out var session) ? new RemoteViewerSession(session) : null)
            .Where(session => session is not null)
            .Cast<RemoteViewerSession>()
            .OrderByDescending(session => session.UpdatedAt)
            .ThenByDescending(session => session.CreatedAt)
            .ToArray();

    private async Task<RunnerEntry> ResolveEntryBySessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (await TryResolveEntryBySessionAsync(sessionId, cancellationToken) is { } entry)
        {
            return entry;
        }

        throw new InvalidOperationException($"Coordinator could not resolve runner ownership for session '{sessionId}'.");
    }

    private async Task<RunnerEntry?> TryResolveEntryBySessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_sessionMachineMap.TryGetValue(sessionId, out var machineId))
        {
            return await EnsureKnownEntryAsync(machineId, cancellationToken);
        }

        var machines = await _registry.GetMachinesAsync(cancellationToken);
        foreach (var machine in machines.Where(machine => machine.IsOnline))
        {
            RunnerEntry entry;
            try
            {
                entry = await EnsureEntryAsync(machine.MachineId, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            IReadOnlyList<TerminalSession> sessions;
            try
            {
                sessions = await InvokeRunnerAsync(entry, "list terminal sessions", client => client.GetSessionsAsync(), retryOnReconnect: true, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            foreach (var session in sessions)
            {
                AnnotateSession(session, machine);
                if (string.Equals(session.Id, sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }
        }

        return null;
    }

    private async Task<RunnerEntry> EnsureKnownEntryAsync(string machineId, CancellationToken cancellationToken)
    {
        var normalizedMachineId = machineId.Trim();
        var machine = await _registry.GetMachineAsync(normalizedMachineId, cancellationToken)
            ?? throw new InvalidOperationException($"Coordinator does not know runner machine '{machineId}'.");

        var entry = _entries.GetOrAdd(normalizedMachineId, static id => new RunnerEntry
        {
            MachineId = id,
            Gate = new SemaphoreSlim(1, 1)
        });

        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            entry.Machine = machine;
            return entry;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private async Task<T> InvokeRunnerAsync<T>(RunnerEntry entry, string operation, Func<IRunnerControlClient, Task<T>> action, bool retryOnReconnect, CancellationToken cancellationToken)
    {
        var initialConnectionId = entry.ConnectionId;
        try
        {
            return await action(GetRunnerClient(entry));
        }
        catch (Exception ex) when (IsRetryableRunnerDisconnect(entry, ex))
        {
            _logger.LogWarning(
                ex,
                "Runner {MachineName} ({MachineId}) disconnected while attempting to {Operation}; checking for a refreshed control connection.",
                entry.Machine?.MachineName ?? entry.MachineId,
                entry.MachineId,
                operation);

            if (!retryOnReconnect)
            {
                throw BuildRunnerDisconnectException(entry, operation, ex);
            }

            return await RetryRunnerInvokeAsync(entry, initialConnectionId, operation, action, cancellationToken, ex);
        }
    }

    private async Task InvokeRunnerAsync(RunnerEntry entry, string operation, Func<IRunnerControlClient, Task> action, bool retryOnReconnect, CancellationToken cancellationToken)
    {
        var initialConnectionId = entry.ConnectionId;
        try
        {
            await action(GetRunnerClient(entry));
        }
        catch (Exception ex) when (IsRetryableRunnerDisconnect(entry, ex))
        {
            _logger.LogWarning(
                ex,
                "Runner {MachineName} ({MachineId}) disconnected while attempting to {Operation}; checking for a refreshed control connection.",
                entry.Machine?.MachineName ?? entry.MachineId,
                entry.MachineId,
                operation);

            if (!retryOnReconnect)
            {
                throw BuildRunnerDisconnectException(entry, operation, ex);
            }

            await RetryRunnerInvokeAsync(entry, initialConnectionId, operation, action, cancellationToken, ex);
        }
    }

    private async Task<T> RetryRunnerInvokeAsync<T>(RunnerEntry entry, string? initialConnectionId, string operation, Func<IRunnerControlClient, Task<T>> action, CancellationToken cancellationToken, Exception originalException)
    {
        var refreshedEntry = await EnsureRefreshedEntryAsync(entry, initialConnectionId, operation, originalException, cancellationToken);
        try
        {
            return await action(GetRunnerClient(refreshedEntry));
        }
        catch (Exception retryException) when (IsRetryableRunnerDisconnect(refreshedEntry, retryException))
        {
            throw BuildRunnerDisconnectException(refreshedEntry, operation, retryException);
        }
    }

    private async Task RetryRunnerInvokeAsync(RunnerEntry entry, string? initialConnectionId, string operation, Func<IRunnerControlClient, Task> action, CancellationToken cancellationToken, Exception originalException)
    {
        var refreshedEntry = await EnsureRefreshedEntryAsync(entry, initialConnectionId, operation, originalException, cancellationToken);
        try
        {
            await action(GetRunnerClient(refreshedEntry));
        }
        catch (Exception retryException) when (IsRetryableRunnerDisconnect(refreshedEntry, retryException))
        {
            throw BuildRunnerDisconnectException(refreshedEntry, operation, retryException);
        }
    }

    private async Task<RunnerEntry> EnsureRefreshedEntryAsync(RunnerEntry entry, string? initialConnectionId, string operation, Exception originalException, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + RunnerReconnectWaitWindow;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var refreshedEntry = await TryEnsureEntryAsync(entry.MachineId, cancellationToken);
            if (refreshedEntry is not null &&
                !string.IsNullOrWhiteSpace(refreshedEntry.ConnectionId) &&
                !string.Equals(refreshedEntry.ConnectionId, initialConnectionId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "Retrying brokered {Operation} on runner {MachineName} ({MachineId}) after control connection moved from {PreviousConnectionId} to {CurrentConnectionId}.",
                    operation,
                    refreshedEntry.Machine?.MachineName ?? refreshedEntry.MachineId,
                    refreshedEntry.MachineId,
                    initialConnectionId ?? "<none>",
                    refreshedEntry.ConnectionId);

                return refreshedEntry;
            }

            await Task.Delay(RunnerReconnectPollInterval, cancellationToken);
        }

        throw BuildRunnerDisconnectException(entry, operation, originalException);
    }

    private static bool IsTransientRunnerDisconnect(Exception exception) =>
        exception is IOException or ObjectDisposedException;

    private static bool IsRetryableRunnerDisconnect(RunnerEntry entry, Exception exception) =>
        IsTransientRunnerDisconnect(exception) || IsMissingRunnerConnection(entry, exception);

    private static bool IsMissingRunnerConnection(RunnerEntry entry, Exception exception) =>
        exception is InvalidOperationException invalidOperationException &&
        string.Equals(
            invalidOperationException.Message,
            $"Runner machine '{entry.Machine?.MachineName ?? entry.MachineId}' does not currently have an active coordinator control connection.",
            StringComparison.Ordinal);

    private static InvalidOperationException BuildRunnerDisconnectException(RunnerEntry entry, string operation, Exception exception) =>
        new(
            $"Runner machine '{entry.Machine?.MachineName ?? entry.MachineId}' disconnected while attempting to {operation}. Retry the request after the runner reconnects.",
            exception);

    private static string NormalizeActorId(string actorId) =>
        string.IsNullOrWhiteSpace(actorId) ? "coordinator" : actorId.Trim();

    private static OrchestrationJob CloneOrchestrationJob(OrchestrationJob job) =>
        new()
        {
            Id = job.Id,
            ProjectId = job.ProjectId,
            ProjectName = job.ProjectName,
            LaunchProfileId = job.LaunchProfileId,
            LaunchProfileName = job.LaunchProfileName,
            Platform = job.Platform,
            Mode = job.Mode,
            LaunchDriver = job.LaunchDriver,
            TargetMachineRole = job.TargetMachineRole,
            TargetMachineId = job.TargetMachineId,
            TargetMachineName = job.TargetMachineName,
            WorkingDirectory = job.WorkingDirectory,
            BuildCommand = job.BuildCommand,
            LaunchCommand = job.LaunchCommand,
            BootstrapCommand = job.BootstrapCommand,
            DebugConfigurationName = job.DebugConfigurationName,
            DeviceSelection = job.DeviceSelection is null
                ? null
                : new VirtualDeviceLaunchSelection
                {
                    CatalogKind = job.DeviceSelection.CatalogKind,
                    TargetPlatform = job.DeviceSelection.TargetPlatform,
                    DeviceId = job.DeviceSelection.DeviceId,
                    ProfileId = job.DeviceSelection.ProfileId,
                    DisplayName = job.DeviceSelection.DisplayName,
                    StartBeforeLaunch = job.DeviceSelection.StartBeforeLaunch,
                    ReuseRunningDevice = job.DeviceSelection.ReuseRunningDevice
                },
            Status = job.Status,
            SessionId = job.SessionId,
            ViewerSessionId = job.ViewerSessionId,
            ExitCode = job.ExitCode,
            StatusMessage = job.StatusMessage,
            CreatedAt = job.CreatedAt,
            UpdatedAt = job.UpdatedAt,
            Steps =
            [
                ..job.Steps.Select(step => new OrchestrationJobStep
                {
                    Name = step.Name,
                    Status = step.Status,
                    Message = step.Message,
                    StartedAt = step.StartedAt,
                    CompletedAt = step.CompletedAt
                })
            ],
            Logs =
            [
                ..job.Logs.Select(log => new OrchestrationJobLogEntry
                {
                    Timestamp = log.Timestamp,
                    Level = log.Level,
                    Message = log.Message,
                    MachineId = log.MachineId
                })
            ]
        };
}
