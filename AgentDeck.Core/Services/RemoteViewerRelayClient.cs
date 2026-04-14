using AgentDeck.Shared.Hubs;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace AgentDeck.Core.Services;

public sealed class RemoteViewerRelayClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly ICoordinatorApiClient _coordinatorClient;
    private readonly IAgentDeckClient _agentClient;
    private HubConnection? _connection;
    private string? _coordinatorUrl;
    private string? _machineId;
    private string? _viewerSessionId;

    public RemoteViewerRelayClient(
        ICoordinatorApiClient coordinatorClient,
        IAgentDeckClient agentClient)
    {
        _coordinatorClient = coordinatorClient;
        _agentClient = agentClient;
    }

    public event Action? Changed;

    public RemoteViewerSession? CurrentSession { get; private set; }

    public MachineRemoteControlState? RemoteControlState { get; private set; }

    public string? CurrentFrameDataUrl { get; private set; }

    public string StatusMessage { get; private set; } = "Idle";

    public bool IsWatching
    {
        get
        {
            var connection = _connection;
            var currentSession = CurrentSession;
            return connection is not null &&
                   connection.State.Equals(Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected) &&
                   currentSession is not null;
        }
    }

    public bool SupportsInAppViewer
    {
        get
        {
            var currentSession = CurrentSession;
            return currentSession?.Provider == RemoteViewerProviderKind.Managed;
        }
    }

    public bool CanSendInput
    {
        get
        {
            var currentSession = CurrentSession;
            var remoteControlState = RemoteControlState;
            return currentSession?.Status == RemoteViewerSessionStatus.Ready &&
                   currentSession.Provider == RemoteViewerProviderKind.Managed &&
                   (remoteControlState is null || IsCurrentCompanionMachineController(remoteControlState));
        }
    }

    public bool CanTakeControlCurrentViewer
    {
        get
        {
            var currentSession = CurrentSession;
            var remoteControlState = RemoteControlState;
            return currentSession is not null &&
                   remoteControlState is not null &&
                   IsCurrentCompanionMachineController(remoteControlState) &&
                   !string.Equals(remoteControlState.ViewerSessionId, currentSession.Id, StringComparison.OrdinalIgnoreCase);
        }
    }

    public async Task LoadAsync(
        string coordinatorUrl,
        string machineId,
        string viewerSessionId,
        CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            _coordinatorUrl = coordinatorUrl;
            _machineId = machineId;
            _viewerSessionId = viewerSessionId;
        }
        finally
        {
            _sync.Release();
        }

        await RefreshAsync(cancellationToken);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        string? coordinatorUrl;
        string? machineId;
        string? viewerSessionId;

        await _sync.WaitAsync(cancellationToken);
        try
        {
            coordinatorUrl = _coordinatorUrl;
            machineId = _machineId;
            viewerSessionId = _viewerSessionId;
        }
        finally
        {
            _sync.Release();
        }

        if (string.IsNullOrWhiteSpace(coordinatorUrl) ||
            string.IsNullOrWhiteSpace(machineId) ||
            string.IsNullOrWhiteSpace(viewerSessionId))
        {
            return;
        }

        var viewerSessionsTask = _coordinatorClient.GetMachineViewerSessionsAsync(coordinatorUrl, machineId, cancellationToken);
        var remoteControlTask = _coordinatorClient.GetMachineRemoteControlStateAsync(coordinatorUrl, machineId, cancellationToken);
        await Task.WhenAll(viewerSessionsTask, remoteControlTask);

        var session = viewerSessionsTask.Result
            .FirstOrDefault(candidate => string.Equals(candidate.Id, viewerSessionId, StringComparison.OrdinalIgnoreCase));

        await _sync.WaitAsync(cancellationToken);
        try
        {
            CurrentSession = session;
            RemoteControlState = remoteControlTask.Result;
            if (session is null)
            {
                CurrentFrameDataUrl = null;
                StatusMessage = "Viewer session not found.";
            }
            else
            {
                StatusMessage = session.StatusMessage ?? $"Viewer session {session.Status}.";
            }
        }
        finally
        {
            _sync.Release();
        }

        if (session is null ||
            session.Status != RemoteViewerSessionStatus.Ready ||
            session.Provider != RemoteViewerProviderKind.Managed)
        {
            await DisconnectConnectionAsync(cancellationToken);
            NotifyChanged();
            return;
        }

        await EnsureConnectionAsync(coordinatorUrl, machineId, session, cancellationToken);
        NotifyChanged();
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await DisconnectConnectionAsync(cancellationToken);

        await _sync.WaitAsync(cancellationToken);
        try
        {
            CurrentSession = null;
            RemoteControlState = null;
            CurrentFrameDataUrl = null;
            StatusMessage = "Disconnected.";
            _coordinatorUrl = null;
            _machineId = null;
            _viewerSessionId = null;
        }
        finally
        {
            _sync.Release();
        }

        NotifyChanged();
    }

    public async Task SendPointerAsync(
        string eventType,
        double x,
        double y,
        string? button,
        int clickCount,
        int wheelDeltaX,
        int wheelDeltaY)
    {
        HubConnection? connection;
        string? machineId;
        string? sessionId;
        var canSendInput = false;

        await _sync.WaitAsync();
        try
        {
            connection = _connection;
            machineId = _machineId;
            sessionId = CurrentSession?.Id;
            canSendInput = CanSendInput;
        }
        finally
        {
            _sync.Release();
        }

        if (!canSendInput ||
            connection is null ||
            string.IsNullOrWhiteSpace(machineId) ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        await connection.InvokeAsync(
            nameof(ICoordinatorViewerHub.SendViewerPointerInputAsync),
            machineId,
            sessionId,
            new RemoteViewerPointerInputEvent(sessionId, eventType, x, y, button, clickCount, wheelDeltaX, wheelDeltaY));
    }

    public async Task SendKeyboardAsync(string eventType, string code, bool alt, bool control, bool shift)
    {
        HubConnection? connection;
        string? machineId;
        string? sessionId;
        var canSendInput = false;

        await _sync.WaitAsync();
        try
        {
            connection = _connection;
            machineId = _machineId;
            sessionId = CurrentSession?.Id;
            canSendInput = CanSendInput;
        }
        finally
        {
            _sync.Release();
        }

        if (!canSendInput ||
            connection is null ||
            string.IsNullOrWhiteSpace(machineId) ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        await connection.InvokeAsync(
            nameof(ICoordinatorViewerHub.SendViewerKeyboardInputAsync),
            machineId,
            sessionId,
            new RemoteViewerKeyboardInputEvent(sessionId, eventType, code, alt, control, shift));
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectConnectionAsync(CancellationToken.None);
        _sync.Dispose();
    }

    private async Task EnsureConnectionAsync(string coordinatorUrl, string machineId, RemoteViewerSession session, CancellationToken cancellationToken)
    {
        var needsReconnect = false;

        await _sync.WaitAsync(cancellationToken);
        try
        {
            needsReconnect =
                _connection is null ||
                _connection.State.Equals(Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Disconnected) ||
                !string.Equals(_viewerSessionId, session.Id, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _sync.Release();
        }

        if (!needsReconnect)
        {
            return;
        }

        await DisconnectConnectionAsync(cancellationToken);

        await _agentClient.ConnectAsync(coordinatorUrl, cancellationToken);
        var companionId = _agentClient.CompanionId
            ?? throw new InvalidOperationException("Coordinator companion identity is required before opening the viewer relay.");
        var connection = BuildConnection(coordinatorUrl, companionId);

        await _sync.WaitAsync(cancellationToken);
        try
        {
            _machineId = machineId;
            _viewerSessionId = session.Id;
            _connection = connection;
            CurrentFrameDataUrl = null;
            StatusMessage = $"Connecting to {session.Target.DisplayName} through the coordinator...";
        }
        finally
        {
            _sync.Release();
        }

        try
        {
            await connection.StartAsync(cancellationToken);
            await connection.InvokeAsync(
                nameof(ICoordinatorViewerHub.JoinViewerSessionAsync),
                machineId,
                session.Id,
                cancellationToken);
        }
        catch
        {
            await DisconnectConnectionAsync(CancellationToken.None);
            throw;
        }
    }

    private HubConnection BuildConnection(string coordinatorUrl, string companionId)
    {
        var connectionUri = $"{coordinatorUrl.TrimEnd('/')}/hubs/viewers?{AgentDeck.Shared.AgentDeckQueryNames.Companion}={Uri.EscapeDataString(companionId)}";
        var connection = new HubConnectionBuilder()
            .WithUrl(connectionUri, options =>
            {
                options.Headers[AgentDeck.Shared.AgentDeckHeaderNames.Companion] = companionId;
                options.Headers[AgentDeck.Shared.AgentDeckHeaderNames.Actor] = companionId;
            })
            .WithAutomaticReconnect()
            .Build();

        connection.On<RemoteViewerSession>(nameof(IViewerHubClient.ViewerSessionUpdatedAsync), session => HandleSessionUpdatedAsync(connection, session));
        connection.On<RemoteViewerRelayFrame>(nameof(IViewerHubClient.ViewerFramePublishedAsync), frame => HandleFramePublishedAsync(connection, frame));
        connection.Reconnected += _ => RejoinViewerSessionAsync(connection);
        connection.Closed += exception => HandleConnectionClosedAsync(connection, exception);
        return connection;
    }

    private async Task HandleSessionUpdatedAsync(HubConnection sourceConnection, RemoteViewerSession session)
    {
        var shouldNotify = false;

        await _sync.WaitAsync();
        try
        {
            if (!ReferenceEquals(_connection, sourceConnection) ||
                !string.Equals(_viewerSessionId, session.Id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            CurrentSession = session;
            StatusMessage = session.StatusMessage ?? $"Viewer session {session.Status}.";
            shouldNotify = true;
        }
        finally
        {
            _sync.Release();
        }

        if (shouldNotify)
        {
            NotifyChanged();
        }
    }

    private async Task HandleFramePublishedAsync(HubConnection sourceConnection, RemoteViewerRelayFrame frame)
    {
        var shouldNotify = false;

        await _sync.WaitAsync();
        try
        {
            if (!ReferenceEquals(_connection, sourceConnection) ||
                !string.Equals(_viewerSessionId, frame.SessionId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            CurrentFrameDataUrl = $"data:{frame.ContentType};base64,{Convert.ToBase64String(frame.Payload)}";
            StatusMessage = $"Streaming {frame.Width}x{frame.Height} frame {frame.SequenceId}.";
            shouldNotify = true;
        }
        finally
        {
            _sync.Release();
        }

        if (shouldNotify)
        {
            NotifyChanged();
        }
    }

    private async Task HandleConnectionClosedAsync(HubConnection sourceConnection, Exception? exception)
    {
        var shouldNotify = false;

        await _sync.WaitAsync();
        try
        {
            if (!ReferenceEquals(_connection, sourceConnection))
            {
                return;
            }

            _connection = null;
            StatusMessage = exception is null
                ? "Viewer connection closed."
                : $"Viewer connection closed: {exception.GetBaseException().Message}";
            shouldNotify = true;
        }
        finally
        {
            _sync.Release();
        }

        if (shouldNotify)
        {
            NotifyChanged();
        }
    }

    private async Task DisconnectConnectionAsync(CancellationToken cancellationToken)
    {
        HubConnection? connection;

        await _sync.WaitAsync(cancellationToken);
        try
        {
            connection = _connection;
            _connection = null;
            CurrentFrameDataUrl = null;
        }
        finally
        {
            _sync.Release();
        }

        if (connection is not null)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_viewerSessionId))
                {
                    await connection.InvokeAsync(nameof(ICoordinatorViewerHub.LeaveViewerSessionAsync), _viewerSessionId, cancellationToken);
                }
            }
            catch
            {
            }

            await connection.DisposeAsync();
        }
    }

    private async Task RejoinViewerSessionAsync(HubConnection sourceConnection)
    {
        string? machineId;
        string? viewerSessionId;

        await _sync.WaitAsync();
        try
        {
            if (!ReferenceEquals(_connection, sourceConnection))
            {
                return;
            }

            machineId = _machineId;
            viewerSessionId = _viewerSessionId;
        }
        finally
        {
            _sync.Release();
        }

        if (string.IsNullOrWhiteSpace(machineId) || string.IsNullOrWhiteSpace(viewerSessionId))
        {
            return;
        }

        await sourceConnection.InvokeAsync(nameof(ICoordinatorViewerHub.JoinViewerSessionAsync), machineId, viewerSessionId);
    }

    private bool IsCurrentCompanionMachineController(MachineRemoteControlState remoteControlState) =>
        !string.IsNullOrWhiteSpace(_agentClient.CompanionId) &&
        string.Equals(remoteControlState.ControllerCompanionId, _agentClient.CompanionId, StringComparison.OrdinalIgnoreCase);

    private void NotifyChanged() => Changed?.Invoke();
}
