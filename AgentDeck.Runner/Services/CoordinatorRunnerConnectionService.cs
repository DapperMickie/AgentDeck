using AgentDeck.Runner.Configuration;
using AgentDeck.Shared;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Hubs;
using AgentDeck.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using RdpPoc.Contracts;

namespace AgentDeck.Runner.Services;

public sealed class CoordinatorRunnerConnectionService : BackgroundService, IAsyncDisposable
{
    private readonly WorkerCoordinatorOptions _options;
    private readonly ITerminalSessionService _terminalSessions;
    private readonly IPtyProcessManager _ptyManager;
    private readonly IAgentSessionStore _sessionStore;
    private readonly IWorkspaceService _workspace;
    private readonly IProjectWorkspaceBootstrapService _projectBootstrap;
    private readonly IMachineCapabilityService _capabilities;
    private readonly IMachineSetupService _setup;
    private readonly IRunnerWorkflowPackService _workflowPacks;
    private readonly IOrchestrationJobService _jobs;
    private readonly IOrchestrationExecutionService _execution;
    private readonly IRemoteViewerSessionService _viewers;
    private readonly IDesktopViewerBootstrapService _desktopBootstrap;
    private readonly IManagedViewerRelayService _managedViewerRelay;
    private readonly IVirtualDeviceCatalogService _devices;
    private readonly IRunnerTrustPolicy _trustPolicy;
    private readonly IRunnerAuditService _audit;
    private readonly ILogger<CoordinatorRunnerConnectionService> _logger;
    private readonly CoordinatorRunnerConnectionState _connectionState;

    public CoordinatorRunnerConnectionService(
        IOptions<WorkerCoordinatorOptions> options,
        ITerminalSessionService terminalSessions,
        IPtyProcessManager ptyManager,
        IAgentSessionStore sessionStore,
        IWorkspaceService workspace,
        IProjectWorkspaceBootstrapService projectBootstrap,
        IMachineCapabilityService capabilities,
        IMachineSetupService setup,
        IRunnerWorkflowPackService workflowPacks,
        IOrchestrationJobService jobs,
        IOrchestrationExecutionService execution,
        IRemoteViewerSessionService viewers,
        IDesktopViewerBootstrapService desktopBootstrap,
        IManagedViewerRelayService managedViewerRelay,
        IVirtualDeviceCatalogService devices,
        IRunnerTrustPolicy trustPolicy,
        IRunnerAuditService audit,
        CoordinatorRunnerConnectionState connectionState,
        ILogger<CoordinatorRunnerConnectionService> logger)
    {
        _options = options.Value;
        _terminalSessions = terminalSessions;
        _ptyManager = ptyManager;
        _sessionStore = sessionStore;
        _workspace = workspace;
        _projectBootstrap = projectBootstrap;
        _capabilities = capabilities;
        _setup = setup;
        _workflowPacks = workflowPacks;
        _jobs = jobs;
        _execution = execution;
        _viewers = viewers;
        _desktopBootstrap = desktopBootstrap;
        _managedViewerRelay = managedViewerRelay;
        _devices = devices;
        _trustPolicy = trustPolicy;
        _audit = audit;
        _connectionState = connectionState;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.CoordinatorUrl))
        {
            return;
        }

        var normalizedCoordinatorUrl = _options.CoordinatorUrl.Trim().TrimEnd('/');
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureConnectedAsync(normalizedCoordinatorUrl, stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Runner control connection to coordinator {CoordinatorUrl} failed; retrying.", normalizedCoordinatorUrl);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connectionState.Connection is not null)
        {
            await _connectionState.Connection.DisposeAsync();
            _connectionState.Connection = null;
        }

        _connectionState.Gate.Dispose();
    }

    private async Task EnsureConnectedAsync(string coordinatorUrl, CancellationToken cancellationToken)
    {
        await _connectionState.Gate.WaitAsync(cancellationToken);
        try
        {
            if (_connectionState.Connection is not null &&
                _connectionState.Connection.State is HubConnectionState.Connected or HubConnectionState.Connecting or HubConnectionState.Reconnecting)
            {
                return;
            }

            if (_connectionState.Connection is not null)
            {
                await _connectionState.Connection.DisposeAsync();
                _connectionState.Connection = null;
            }

            var hubUrl = $"{coordinatorUrl}/hubs/runners?{AgentDeckQueryNames.Machine}={Uri.EscapeDataString(_options.MachineId)}";
            var connection = new HubConnectionBuilder()
                .WithUrl(hubUrl, options =>
                {
                    options.Headers[AgentDeckHeaderNames.Machine] = _options.MachineId;
                })
                .WithAutomaticReconnect()
                .Build();

            RegisterHandlers(connection);
            await connection.StartAsync(cancellationToken);
            _connectionState.Connection = connection;
            _logger.LogInformation("Connected runner control channel to coordinator at {CoordinatorUrl} for machine {MachineId}", coordinatorUrl, _options.MachineId);
        }
        finally
        {
            _connectionState.Gate.Release();
        }
    }

    private void RegisterHandlers(HubConnection connection)
    {
        connection.On<CreateTerminalRequest, Task<TerminalSession>>(nameof(IRunnerControlClient.CreateSessionAsync),
            request => _terminalSessions.CreateSessionAsync(request));

        connection.On<string, Task>(nameof(IRunnerControlClient.CloseSessionAsync), async sessionId =>
        {
            var session = _sessionStore.Get(sessionId);
            if (session is null)
            {
                return;
            }

            await _ptyManager.KillAsync(sessionId);
            _sessionStore.Remove(sessionId);
        });

        connection.On<Task<IReadOnlyList<TerminalSession>>>(nameof(IRunnerControlClient.GetSessionsAsync),
            () => Task.FromResult(_sessionStore.GetAll()));

        connection.On<string, string, Task>(nameof(IRunnerControlClient.SendInputAsync),
            (sessionId, data) => _ptyManager.WriteAsync(sessionId, data));

        connection.On<string, int, int, Task>(nameof(IRunnerControlClient.ResizeTerminalAsync),
            (sessionId, cols, rows) => _ptyManager.ResizeAsync(sessionId, cols, rows));

        connection.On<Task<WorkspaceInfo?>>(nameof(IRunnerControlClient.GetWorkspaceAsync),
            () => Task.FromResult<WorkspaceInfo?>(_workspace.GetWorkspaceInfo()));

        connection.On<OpenProjectOnRunnerRequest, string, Task<OpenProjectOnRunnerResult?>>(nameof(IRunnerControlClient.OpenProjectAsync),
            (request, actorId) => OpenProjectAsync(request, actorId));

        connection.On<Task<MachineCapabilitiesSnapshot?>>(nameof(IRunnerControlClient.GetMachineCapabilitiesAsync),
            () => _capabilities.GetSnapshotAsync());

        connection.On<string, MachineCapabilityInstallRequest, string, Task<MachineCapabilityInstallResult?>>(nameof(IRunnerControlClient.InstallMachineCapabilityAsync),
            (capabilityId, request, actorId) => InstallMachineCapabilityAsync(capabilityId, request, actorId));

        connection.On<string, string, Task<MachineCapabilityInstallResult?>>(nameof(IRunnerControlClient.UpdateMachineCapabilityAsync),
            (capabilityId, actorId) => UpdateMachineCapabilityAsync(capabilityId, actorId));

        connection.On<string, Task>(nameof(IRunnerControlClient.RetryMachineWorkflowPackAsync),
            actorId => RetryWorkflowPackAsync(actorId));

        connection.On<Task<IReadOnlyList<OrchestrationJob>>>(nameof(IRunnerControlClient.GetOrchestrationJobsAsync),
            () => Task.FromResult(_jobs.GetAll()));

        connection.On<CreateOrchestrationJobRequest, string, Task<OrchestrationJob?>>(nameof(IRunnerControlClient.QueueOrchestrationJobAsync),
            (request, actorId) => QueueOrchestrationJobAsync(request, actorId));

        connection.On<string, string, Task<OrchestrationJob?>>(nameof(IRunnerControlClient.CancelOrchestrationJobAsync),
            (jobId, actorId) => CancelOrchestrationJobAsync(jobId, actorId));

        connection.On<Task<IReadOnlyList<RemoteViewerSession>>>(nameof(IRunnerControlClient.GetViewerSessionsAsync),
            () => Task.FromResult(_viewers.GetAll()));

        connection.On<CreateRemoteViewerSessionRequest, string, Task<RemoteViewerSession>>(nameof(IRunnerControlClient.CreateViewerSessionAsync),
            (request, actorId) => CreateViewerSessionAsync(request, actorId));

        connection.On<string, string, Task<RemoteViewerSession?>>(nameof(IRunnerControlClient.CloseViewerSessionAsync),
            (viewerSessionId, actorId) => CloseViewerSessionAsync(viewerSessionId, actorId));

        connection.On<string, string, RemoteViewerPointerInputEvent, Task>(nameof(IRunnerControlClient.SendViewerPointerInputAsync),
            (viewerSessionId, actorId, input) => SendViewerPointerInputAsync(viewerSessionId, actorId, input));

        connection.On<string, string, RemoteViewerKeyboardInputEvent, Task>(nameof(IRunnerControlClient.SendViewerKeyboardInputAsync),
            (viewerSessionId, actorId, input) => SendViewerKeyboardInputAsync(viewerSessionId, actorId, input));

        connection.On<Task<IReadOnlyList<VirtualDeviceCatalogSnapshot>>>(nameof(IRunnerControlClient.GetVirtualDeviceCatalogsAsync),
            () => _devices.GetCatalogsAsync());

        connection.On<VirtualDeviceLaunchSelection, Task<VirtualDeviceLaunchResolution?>>(nameof(IRunnerControlClient.ResolveVirtualDeviceAsync),
            selection => ResolveVirtualDeviceAsync(selection));
    }

    private async Task<OpenProjectOnRunnerResult?> OpenProjectAsync(OpenProjectOnRunnerRequest request, string actorId)
    {
        return await _projectBootstrap.OpenProjectAsync(request);
    }

    private async Task<MachineCapabilityInstallResult?> InstallMachineCapabilityAsync(string capabilityId, MachineCapabilityInstallRequest request, string actorId)
    {
        var decision = _trustPolicy.Evaluate(RunnerActionContext.ForCoordinator(actorId), "capability.install", "capability", capabilityId, capabilityId);
        if (!decision.Allowed)
        {
            _audit.Record(decision, RunnerAuditOutcome.Denied, decision.Message);
            throw new InvalidOperationException(decision.Message);
        }

        var result = await _setup.InstallCapabilityAsync(capabilityId, request.Version);
        _audit.Record(decision, result.Succeeded ? RunnerAuditOutcome.Succeeded : RunnerAuditOutcome.Failed, result.Message);
        return result;
    }

    private async Task<MachineCapabilityInstallResult?> UpdateMachineCapabilityAsync(string capabilityId, string actorId)
    {
        var decision = _trustPolicy.Evaluate(RunnerActionContext.ForCoordinator(actorId), "capability.update", "capability", capabilityId, capabilityId);
        if (!decision.Allowed)
        {
            _audit.Record(decision, RunnerAuditOutcome.Denied, decision.Message);
            throw new InvalidOperationException(decision.Message);
        }

        var result = await _setup.UpdateCapabilityAsync(capabilityId);
        _audit.Record(decision, result.Succeeded ? RunnerAuditOutcome.Succeeded : RunnerAuditOutcome.Failed, result.Message);
        return result;
    }

    private async Task RetryWorkflowPackAsync(string actorId)
    {
        var currentStatus = await _workflowPacks.GetCurrentStatusAsync();
        var targetId = currentStatus?.PackId ?? "current";
        var targetDisplayName = currentStatus?.PackVersion is { Length: > 0 }
            ? $"{targetId}@{currentStatus.PackVersion}"
            : targetId;
        var decision = _trustPolicy.Evaluate(RunnerActionContext.ForCoordinator(actorId), "workflow-pack.retry", "workflow-pack", targetId, targetDisplayName);
        if (!decision.Allowed)
        {
            _audit.Record(decision, RunnerAuditOutcome.Denied, decision.Message);
            throw new InvalidOperationException(decision.Message);
        }

        await _workflowPacks.ResetCurrentStatusAsync();
        _audit.Record(decision, RunnerAuditOutcome.Succeeded, $"Cleared workflow pack state for {targetDisplayName}.");
    }

    private Task<OrchestrationJob?> QueueOrchestrationJobAsync(CreateOrchestrationJobRequest request, string actorId)
    {
        var decision = _trustPolicy.Evaluate(RunnerActionContext.ForCoordinator(actorId), "orchestration.queue", "project", request.ProjectId, request.ProjectName);
        if (!decision.Allowed)
        {
            _audit.Record(decision, RunnerAuditOutcome.Denied, decision.Message);
            throw new InvalidOperationException(decision.Message);
        }

        if (request.DeviceSelection is not null && !request.DeviceSelection.HasTarget)
        {
            _audit.Record(decision, RunnerAuditOutcome.Failed, "Device selection must include either a device ID or a profile ID.");
            throw new InvalidOperationException("Device selection must include either a device ID or a profile ID.");
        }

        var job = _jobs.Queue(request);
        _execution.Start(job.Id);
        _audit.Record(decision, RunnerAuditOutcome.Succeeded, $"Queued orchestration job '{job.Id}' for launch profile '{request.LaunchProfileId}'.");
        return Task.FromResult<OrchestrationJob?>(job);
    }

    private async Task<OrchestrationJob?> CancelOrchestrationJobAsync(string jobId, string actorId)
    {
        var decision = _trustPolicy.Evaluate(RunnerActionContext.ForCoordinator(actorId), "orchestration.cancel", "orchestration-job", jobId);
        if (!decision.Allowed)
        {
            _audit.Record(decision, RunnerAuditOutcome.Denied, decision.Message);
            throw new InvalidOperationException(decision.Message);
        }

        if (_jobs.RequestCancellation(jobId) is null)
        {
            _audit.Record(decision, RunnerAuditOutcome.Failed, "The orchestration job was not found.");
            return null;
        }

        await _execution.RequestCancellationAsync(jobId);
        _audit.Record(decision, RunnerAuditOutcome.Succeeded, $"Requested cancellation for orchestration job '{jobId}'.");
        return _jobs.Get(jobId);
    }

    private async Task<RemoteViewerSession> CreateViewerSessionAsync(CreateRemoteViewerSessionRequest request, string actorId)
    {
        var action = request.Target.Kind == RemoteViewerTargetKind.Desktop
            ? "viewer.desktop.create"
            : "viewer.create";
        var decision = _trustPolicy.Evaluate(
            RunnerActionContext.ForCoordinator(actorId),
            action,
            request.Target.Kind.ToString(),
            request.JobId ?? request.Target.JobId,
            request.Target.DisplayName);
        if (!decision.Allowed)
        {
            _audit.Record(decision, RunnerAuditOutcome.Denied, decision.Message);
            throw new InvalidOperationException(decision.Message);
        }

        var session = _viewers.Create(request);
        session = await _desktopBootstrap.BootstrapAsync(
            session.Id,
            connectionBaseUri: _options.CoordinatorUrl?.Trim().TrimEnd('/'),
            cancellationToken: CancellationToken.None) ?? session;
        _audit.Record(
            decision,
            session.Status == RemoteViewerSessionStatus.Failed ? RunnerAuditOutcome.Failed : RunnerAuditOutcome.Succeeded,
            session.StatusMessage ?? $"Created viewer session '{session.Id}'.");
        return session;
    }

    private async Task<RemoteViewerSession?> CloseViewerSessionAsync(string viewerSessionId, string actorId)
    {
        var decision = _trustPolicy.Evaluate(RunnerActionContext.ForCoordinator(actorId), "viewer.close", "viewer-session", viewerSessionId);
        if (!decision.Allowed)
        {
            _audit.Record(decision, RunnerAuditOutcome.Denied, decision.Message);
            throw new InvalidOperationException(decision.Message);
        }

        var result = await _desktopBootstrap.CloseAsync(viewerSessionId);
        if (result.Session is not null)
        {
            _audit.Record(
                decision,
                result.Outcome == RemoteViewerSessionMutationOutcome.Updated ? RunnerAuditOutcome.Succeeded : RunnerAuditOutcome.Failed,
                result.Session.StatusMessage);
        }

        return result.Outcome switch
        {
            RemoteViewerSessionMutationOutcome.Updated when result.Session is not null => result.Session,
            RemoteViewerSessionMutationOutcome.InvalidTransition when result.Session is not null => result.Session,
            _ => null
        };
    }

    private async Task SendViewerPointerInputAsync(string viewerSessionId, string actorId, RemoteViewerPointerInputEvent input)
    {
        var session = _viewers.Get(viewerSessionId)
            ?? throw new InvalidOperationException($"Viewer session '{viewerSessionId}' was not found.");
        if (session.Provider != AgentDeck.Shared.Enums.RemoteViewerProviderKind.Managed)
        {
            throw new InvalidOperationException($"Viewer session '{viewerSessionId}' is not backed by the managed relay.");
        }

        await _managedViewerRelay.SendPointerInputAsync(
            viewerSessionId,
            new PointerInputEvent(viewerSessionId, input.EventType, input.X, input.Y, input.Button, input.ClickCount, input.WheelDeltaX, input.WheelDeltaY));
    }

    private async Task SendViewerKeyboardInputAsync(string viewerSessionId, string actorId, RemoteViewerKeyboardInputEvent input)
    {
        var session = _viewers.Get(viewerSessionId)
            ?? throw new InvalidOperationException($"Viewer session '{viewerSessionId}' was not found.");
        if (session.Provider != AgentDeck.Shared.Enums.RemoteViewerProviderKind.Managed)
        {
            throw new InvalidOperationException($"Viewer session '{viewerSessionId}' is not backed by the managed relay.");
        }

        await _managedViewerRelay.SendKeyboardInputAsync(
            viewerSessionId,
            new KeyboardInputEvent(viewerSessionId, input.EventType, input.Code, input.Alt, input.Control, input.Shift));
    }

    private async Task<VirtualDeviceLaunchResolution?> ResolveVirtualDeviceAsync(VirtualDeviceLaunchSelection selection)
    {
        if (!selection.HasTarget)
        {
            throw new InvalidOperationException("Device selection must include either a device ID or a profile ID.");
        }

        return await _devices.ResolveSelectionAsync(selection);
    }
}
