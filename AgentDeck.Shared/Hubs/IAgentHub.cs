using AgentDeck.Shared.Models;

namespace AgentDeck.Shared.Hubs;

/// <summary>Server-side SignalR hub contract for the AgentDeck runner.</summary>
public interface IAgentHub
{
    /// <summary>Subscribe this connection to output events from the given session.</summary>
    Task JoinSessionAsync(string sessionId);

    /// <summary>Unsubscribe this connection from the given session's output events.</summary>
    Task LeaveSessionAsync(string sessionId);

    /// <summary>Send raw keyboard input to a running terminal session.</summary>
    Task SendInputAsync(string sessionId, string data);

    /// <summary>Notify the runner that the terminal viewport has been resized.</summary>
    Task ResizeTerminalAsync(string sessionId, int cols, int rows);

    /// <summary>Create a new terminal session and return its descriptor.</summary>
    Task<TerminalSession> CreateSessionAsync(CreateTerminalRequest request);

    /// <summary>Close and remove a terminal session.</summary>
    Task CloseSessionAsync(string sessionId);

    /// <summary>Return the current list of all active terminal sessions.</summary>
    Task<IReadOnlyList<TerminalSession>> GetSessionsAsync();
}
