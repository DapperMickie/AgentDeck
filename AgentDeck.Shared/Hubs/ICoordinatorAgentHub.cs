using AgentDeck.Shared.Models;

namespace AgentDeck.Shared.Hubs;

/// <summary>Server-side SignalR hub contract for coordinator-brokered runner control.</summary>
public interface ICoordinatorAgentHub
{
    /// <summary>Subscribe this connection to output events from the given session.</summary>
    Task JoinSessionAsync(string sessionId);

    /// <summary>Unsubscribe this connection from the given session's output events.</summary>
    Task LeaveSessionAsync(string sessionId);

    /// <summary>Send raw keyboard input to a running terminal session.</summary>
    Task SendInputAsync(string sessionId, string data);

    /// <summary>Notify the runner that the terminal viewport has been resized.</summary>
    Task ResizeTerminalAsync(string sessionId, int cols, int rows);

    /// <summary>Create a new terminal session on the selected runner machine.</summary>
    Task<TerminalSession> CreateSessionAsync(string machineId, CreateTerminalRequest request);

    /// <summary>Close and remove a terminal session.</summary>
    Task CloseSessionAsync(string sessionId);

    /// <summary>Return the current list of active terminal sessions on the selected runner machine.</summary>
    Task<IReadOnlyList<TerminalSession>> GetSessionsAsync(string machineId);
}
