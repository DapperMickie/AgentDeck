using AgentDeck.Shared.Models;

namespace AgentDeck.Core.Services;

/// <inheritdoc />
public sealed class SessionStateService : ISessionStateService
{
    private const int MaxBufferedCharacters = 262144;

    private sealed class TerminalBufferState
    {
        public string Content { get; set; } = string.Empty;
        public long Sequence { get; set; }
    }

    private readonly Lock _lock = new();
    private readonly List<TerminalSession> _sessions = [];
    private readonly Dictionary<string, TerminalBufferState> _buffers = new(StringComparer.OrdinalIgnoreCase);

    // Snapshot exposed to UI — replaced atomically under lock
    public IReadOnlyList<TerminalSession> Sessions { get; private set; } = [];

    public event EventHandler? SessionsChanged;
    public event EventHandler<TerminalOutputChunk>? OutputAppended;

    public void Sync(IReadOnlyList<TerminalSession> sessions)
    {
        lock (_lock)
        {
            _sessions.Clear();
            _sessions.AddRange(sessions);
            Sessions = [.. _sessions];

            var activeSessionIds = sessions
                .Select(session => session.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var sessionId in _buffers.Keys.Where(sessionId => !activeSessionIds.Contains(sessionId)).ToArray())
            {
                _buffers.Remove(sessionId);
            }
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
            _buffers.Remove(sessionId);
            if (removed)
            {
                Sessions = [.. _sessions];
            }
        }
        if (removed) SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AppendOutput(TerminalOutput output)
    {
        TerminalOutputChunk? chunk = null;

        lock (_lock)
        {
            if (!_buffers.TryGetValue(output.SessionId, out var buffer))
            {
                buffer = new TerminalBufferState();
                _buffers[output.SessionId] = buffer;
            }

            buffer.Sequence++;
            buffer.Content = TruncateBuffer(buffer.Content + output.Data);
            chunk = new TerminalOutputChunk
            {
                SessionId = output.SessionId,
                Data = output.Data,
                Sequence = buffer.Sequence
            };
        }

        OutputAppended?.Invoke(this, chunk);
    }

    public TerminalOutputSnapshot GetOutputSnapshot(string sessionId)
    {
        lock (_lock)
        {
            return _buffers.TryGetValue(sessionId, out var buffer)
                ? new TerminalOutputSnapshot
                {
                    Content = buffer.Content,
                    Sequence = buffer.Sequence
                }
                : new TerminalOutputSnapshot();
        }
    }

    private static string TruncateBuffer(string content) =>
        content.Length <= MaxBufferedCharacters
            ? content
            : content[^MaxBufferedCharacters..];
}
