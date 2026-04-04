using System.Collections.Concurrent;
using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

/// <inheritdoc />
public sealed class AgentSessionStore : IAgentSessionStore
{
    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new();

    public void Add(TerminalSession session) => _sessions[session.Id] = session;
    public TerminalSession? Get(string sessionId) => _sessions.TryGetValue(sessionId, out var s) ? s : null;
    public void Update(TerminalSession session) => _sessions[session.Id] = session;
    public bool Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);
    public IReadOnlyList<TerminalSession> GetAll() => [.. _sessions.Values];
}
