using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

/// <summary>Thread-safe in-memory registry of active terminal sessions.</summary>
public interface IAgentSessionStore
{
    void Add(TerminalSession session);
    TerminalSession? Get(string sessionId);
    void Update(TerminalSession session);
    bool Remove(string sessionId);
    IReadOnlyList<TerminalSession> GetAll();
}
