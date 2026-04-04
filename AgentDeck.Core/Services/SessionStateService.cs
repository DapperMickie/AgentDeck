using AgentDeck.Shared.Models;

namespace AgentDeck.Core.Services;

/// <inheritdoc />
public sealed class SessionStateService : ISessionStateService
{
    private readonly Lock _lock = new();
    private readonly List<TerminalSession> _sessions = [];

    // Snapshot exposed to UI — replaced atomically under lock
    public IReadOnlyList<TerminalSession> Sessions { get; private set; } = [];

    public event EventHandler? SessionsChanged;

    public void Sync(IReadOnlyList<TerminalSession> sessions)
    {
        lock (_lock)
        {
            _sessions.Clear();
            _sessions.AddRange(sessions);
            Sessions = [.. _sessions];
        }
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AddOrUpdate(TerminalSession session)
    {
        lock (_lock)
        {
            var index = _sessions.FindIndex(s => s.Id == session.Id);
            if (index >= 0) _sessions[index] = session;
            else _sessions.Add(session);
            Sessions = [.. _sessions];
        }
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string sessionId)
    {
        bool removed;
        lock (_lock)
        {
            removed = _sessions.RemoveAll(s => s.Id == sessionId) > 0;
            if (removed) Sessions = [.. _sessions];
        }
        if (removed) SessionsChanged?.Invoke(this, EventArgs.Empty);
    }
}
