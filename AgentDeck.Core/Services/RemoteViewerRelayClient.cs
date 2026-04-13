using AgentDeck.Core.Models;
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
    private string? _viewerAccessToken;
    private string? _connectionUri;

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

    public bool IsWatching =>
        _connection is not null &&
        _connection.State.Equals(HubConnectionState.Connected) &&
        CurrentSession is not null &&
        !string.IsNullOrWhiteSpace(_viewerAccessToken);

    public bool SupportsInAppViewer =>
        CurrentSession?.Provider == RemoteViewerProviderKind.Managed &&
        !string.IsNullOrWhiteSpace(CurrentSession.ConnectionUri) &&
        !string.IsNullOrWhiteSpace(CurrentSession.AccessToken);

    public bool CanSendInput =>
        CurrentSession?.Status == RemoteViewerSessionStatus.Ready &&
        SupportsInAppViewer &&
        (RemoteControlState is null || IsCurrentCompanionController(RemoteControlState, CurrentSession.Id));

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
            session.Provider != RemoteViewerProviderKind.Managed ||
            string.IsNullOrWhiteSpace(session.ConnectionUri) ||
            string.IsNullOrWhiteSpace(session.AccessToken))
        {
            await DisconnectConnectionAsync(cancellationToken);
            NotifyChanged();
            return;
        }

        await EnsureConnectionAsync(session, cancellationToken);
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
        string? viewerAccessToken;
        string? sessionId;
        var canSendInput = false;

        await _sync.WaitAsync();
        try
        {
            connection = _connection;
            viewerAccessToken = _viewerAccessToken;
            sessionId = CurrentSession?.Id;
            canSendInput = CanSendInput;
        }
        finally
        {
            _sync.Release();
        }

        if (!canSendInput ||
            connection is null ||
            string.IsNullOrWhiteSpace(viewerAccessToken) ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        await connection.InvokeAsync(
            "SendPointerInput",
            sessionId,
            viewerAccessToken,
            new RemoteViewerPointerInputEvent(sessionId, eventType, x, y, button, clickCount, wheelDeltaX, wheelDeltaY));
    }

    public async Task SendKeyboardAsync(string eventType, string code, bool alt, bool control, bool shift)
    {
        HubConnection? connection;
        string? viewerAccessToken;
        string? sessionId;
        var canSendInput = false;

        await _sync.WaitAsync();
        try
        {
            connection = _connection;
            viewerAccessToken = _viewerAccessToken;
            sessionId = CurrentSession?.Id;
            canSendInput = CanSendInput;
        }
        finally
        {
            _sync.Release();
        }

        if (!canSendInput ||
            connection is null ||
            string.IsNullOrWhiteSpace(viewerAccessToken) ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        await connection.InvokeAsync(
            "SendKeyboardInput",
            sessionId,
            viewerAccessToken,
            new RemoteViewerKeyboardInputEvent(sessionId, eventType, code, alt, control, shift));
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectConnectionAsync(CancellationToken.None);
        _sync.Dispose();
    }

    private async Task EnsureConnectionAsync(RemoteViewerSession session, CancellationToken cancellationToken)
    {
        var needsReconnect = false;

        await _sync.WaitAsync(cancellationToken);
        try
        {
            needsReconnect =
                _connection is null ||
                _connection.State.Equals(HubConnectionState.Disconnected) ||
                !string.Equals(_viewerSessionId, session.Id, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_connectionUri, session.ConnectionUri, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_viewerAccessToken, session.AccessToken, StringComparison.Ordinal);
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

        var connection = BuildConnection(session.ConnectionUri!);

        await _sync.WaitAsync(cancellationToken);
        try
        {
            _connection = connection;
            _connectionUri = session.ConnectionUri;
            _viewerAccessToken = session.AccessToken;
            CurrentFrameDataUrl = null;
            StatusMessage = $"Connecting to {session.Target.DisplayName}...";
        }
        finally
        {
            _sync.Release();
        }

        try
        {
            await connection.StartAsync(cancellationToken);
            await connection.InvokeAsync(
                "JoinSessionAsViewer",
                session.Id,
                session.AccessToken!,
                cancellationToken);
        }
        catch
        {
            await DisconnectConnectionAsync(CancellationToken.None);
            throw;
        }
    }

    private HubConnection BuildConnection(string connectionUri)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(connectionUri)
            .WithAutomaticReconnect()
            .Build();

        connection.On<RemoteViewerSession>("SessionUpdated", session => HandleSessionUpdatedAsync(connection, session));
        connection.On<RemoteViewerRelayFrame>("FramePublished", frame => HandleFramePublishedAsync(connection, frame));
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
            _connectionUri = null;
            _viewerAccessToken = null;
            CurrentFrameDataUrl = null;
        }
        finally
        {
            _sync.Release();
        }

        if (connection is not null)
        {
            await connection.DisposeAsync();
        }
    }

    private bool IsCurrentCompanionController(MachineRemoteControlState remoteControlState, string viewerSessionId) =>
        !string.IsNullOrWhiteSpace(_agentClient.CompanionId) &&
        string.Equals(remoteControlState.ControllerCompanionId, _agentClient.CompanionId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(remoteControlState.ViewerSessionId, viewerSessionId, StringComparison.OrdinalIgnoreCase);

    private void NotifyChanged() => Changed?.Invoke();
}
