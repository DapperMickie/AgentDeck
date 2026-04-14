using AgentDeck.Shared.Models;

namespace AgentDeck.Core.Services;

/// <summary>Maintains the in-memory list of terminal sessions known to the companion app.</summary>
public interface ISessionStateService
{
    IReadOnlyList<TerminalSession> Sessions { get; }
    event EventHandler? SessionsChanged;
    event EventHandler<TerminalOutputChunk>? OutputAppended;
    void Sync(IReadOnlyList<TerminalSession> sessions);
    void AddOrUpdate(TerminalSession session);
    void Remove(string sessionId);
    void AppendOutput(TerminalOutput output);
    TerminalOutputSnapshot GetOutputSnapshot(string sessionId);
}

public sealed class TerminalOutputSnapshot
{
    public string Content { get; init; } = string.Empty;
    public long Sequence { get; init; }
}

public sealed class TerminalOutputChunk
{
    public required string SessionId { get; init; }
    public required string Data { get; init; }
    public required long Sequence { get; init; }
}
