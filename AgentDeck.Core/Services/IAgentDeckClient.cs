using AgentDeck.Shared.Models;

namespace AgentDeck.Core.Services;

/// <summary>Connection state of the SignalR link to the runner.</summary>
public enum HubConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting
}

/// <summary>Client-side SignalR connection to the AgentDeck runner.</summary>
public interface IAgentDeckClient
{
    /// <summary>Current connection state.</summary>
    HubConnectionState ConnectionState { get; }

    /// <summary>Fired whenever the connection state changes.</summary>
    event EventHandler<HubConnectionState>? ConnectionStateChanged;

    /// <summary>Fired when terminal output is received for any subscribed session.</summary>
    event EventHandler<TerminalOutput>? OutputReceived;

    /// <summary>Fired when the runner reports a new session was created.</summary>
    event EventHandler<TerminalSession>? SessionCreated;

    /// <summary>Fired when a session's state has changed.</summary>
    event EventHandler<TerminalSession>? SessionUpdated;

    /// <summary>Fired when a session has been closed.</summary>
    event EventHandler<string>? SessionClosed;

    /// <summary>Connect to the runner at the given hub URL.</summary>
    Task ConnectAsync(string hubUrl, CancellationToken cancellationToken = default);

    /// <summary>Disconnect from the runner.</summary>
    Task DisconnectAsync();

    /// <summary>Subscribe to output from a specific session.</summary>
    Task JoinSessionAsync(string sessionId);

    /// <summary>Unsubscribe from a specific session's output.</summary>
    Task LeaveSessionAsync(string sessionId);

    /// <summary>Send raw input to a terminal session.</summary>
    Task SendInputAsync(string sessionId, string data);

    /// <summary>Notify the runner the terminal has been resized.</summary>
    Task ResizeTerminalAsync(string sessionId, int cols, int rows);

    /// <summary>Create a new terminal session on the runner.</summary>
    Task<TerminalSession?> CreateSessionAsync(CreateTerminalRequest request);

    /// <summary>Close a terminal session on the runner.</summary>
    Task CloseSessionAsync(string sessionId);

    /// <summary>Get all active sessions from the runner.</summary>
    Task<IReadOnlyList<TerminalSession>> GetSessionsAsync();

    /// <summary>Get workspace information from the runner via REST.</summary>
    Task<WorkspaceInfo?> GetWorkspaceAsync(CancellationToken ct = default);

    /// <summary>Get supported CLI/SDK detection results from the runner via REST.</summary>
    Task<MachineCapabilitiesSnapshot?> GetMachineCapabilitiesAsync(CancellationToken ct = default);

    /// <summary>Install a supported CLI or SDK on the runner machine via REST.</summary>
    Task<MachineCapabilityInstallResult?> InstallMachineCapabilityAsync(string capabilityId, CancellationToken ct = default);
}
