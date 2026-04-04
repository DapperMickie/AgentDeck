using AgentDeck.Shared.Models;

namespace AgentDeck.Core.Services;

/// <summary>Maintains the in-memory list of terminal sessions known to the companion app.</summary>
public interface ISessionStateService
{
    IReadOnlyList<TerminalSession> Sessions { get; }
    event EventHandler? SessionsChanged;
    void Sync(IReadOnlyList<TerminalSession> sessions);
    void AddOrUpdate(TerminalSession session);
    void Remove(string sessionId);
}
