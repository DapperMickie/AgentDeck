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

/// <summary>Client-side SignalR connection to the AgentDeck coordinator broker.</summary>
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

    /// <summary>Connect to the coordinator at the given base URL.</summary>
    Task ConnectAsync(string coordinatorUrl, CancellationToken cancellationToken = default);

    /// <summary>Disconnect from the runner.</summary>
    Task DisconnectAsync();

    /// <summary>Mark this companion as attached to a runner machine through the coordinator.</summary>
    Task AttachMachineAsync(string machineId);

    /// <summary>Mark this companion as detached from a runner machine through the coordinator.</summary>
    Task DetachMachineAsync(string machineId);

    /// <summary>Subscribe to output from a specific session.</summary>
    Task JoinSessionAsync(string sessionId);

    /// <summary>Unsubscribe from a specific session's output.</summary>
    Task LeaveSessionAsync(string sessionId);

    /// <summary>Send raw input to a terminal session.</summary>
    Task SendInputAsync(string sessionId, string data);

    /// <summary>Notify the runner the terminal has been resized.</summary>
    Task ResizeTerminalAsync(string sessionId, int cols, int rows);

    /// <summary>Create a new terminal session on the selected runner machine through the coordinator.</summary>
    Task<TerminalSession?> CreateSessionAsync(string machineId, CreateTerminalRequest request);

    /// <summary>Close a terminal session on the runner.</summary>
    Task CloseSessionAsync(string sessionId);

    /// <summary>Get all active sessions from the selected runner machine through the coordinator.</summary>
    Task<IReadOnlyList<TerminalSession>> GetSessionsAsync(string machineId);

    /// <summary>Get workspace information for the selected runner machine via the coordinator.</summary>
    Task<WorkspaceInfo?> GetWorkspaceAsync(string machineId, CancellationToken ct = default);

    /// <summary>Get supported CLI/SDK detection results for the selected runner machine via the coordinator.</summary>
    Task<MachineCapabilitiesSnapshot?> GetMachineCapabilitiesAsync(string machineId, CancellationToken ct = default);

    /// <summary>Install a supported CLI or SDK on the selected runner machine via the coordinator.</summary>
    Task<MachineCapabilityInstallResult?> InstallMachineCapabilityAsync(string machineId, string capabilityId, string? version = null, CancellationToken ct = default);

    /// <summary>Update a supported CLI on the selected runner machine via the coordinator.</summary>
    Task<MachineCapabilityInstallResult?> UpdateMachineCapabilityAsync(string machineId, string capabilityId, CancellationToken ct = default);
}
