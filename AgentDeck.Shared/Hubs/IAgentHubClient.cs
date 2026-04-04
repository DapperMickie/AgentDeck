using AgentDeck.Shared.Models;

namespace AgentDeck.Shared.Hubs;

/// <summary>Callbacks that the SignalR hub pushes to connected clients.</summary>
public interface IAgentHubClient
{
    /// <summary>Called when terminal output is available for a subscribed session.</summary>
    Task ReceiveOutputAsync(TerminalOutput output);

    /// <summary>Called when a new terminal session has been created on the runner.</summary>
    Task SessionCreatedAsync(TerminalSession session);

    /// <summary>Called when an existing session's state has changed.</summary>
    Task SessionUpdatedAsync(TerminalSession session);

    /// <summary>Called when a terminal session has been closed and removed.</summary>
    Task SessionClosedAsync(string sessionId);
}
