using AgentDeck.Shared.Models;

namespace AgentDeck.Core.Services;

/// <inheritdoc />
public sealed class SessionStateService : ISessionStateService
{
    private readonly List<TerminalSession> _sessions = [];

    public IReadOnlyList<TerminalSession> Sessions => _sessions;
    public event EventHandler? SessionsChanged;

    public void Sync(IReadOnlyList<TerminalSession> sessions)
    {
        _sessions.Clear();
        _sessions.AddRange(sessions);
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AddOrUpdate(TerminalSession session)
    {
        var index = _sessions.FindIndex(s => s.Id == session.Id);
        if (index >= 0) _sessions[index] = session;
        else _sessions.Add(session);
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string sessionId)
    {
        var removed = _sessions.RemoveAll(s => s.Id == sessionId);
        if (removed > 0) SessionsChanged?.Invoke(this, EventArgs.Empty);
    }
}
