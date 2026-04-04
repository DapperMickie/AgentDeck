namespace AgentDeck.Shared.Models;

/// <summary>A chunk of raw terminal output data from a session.</summary>
public sealed class TerminalOutput
{
    /// <summary>The session that produced this output.</summary>
    public required string SessionId { get; init; }

    /// <summary>Raw terminal data (may contain ANSI escape sequences).</summary>
    public required string Data { get; init; }

    /// <summary>UTC timestamp when this output was captured.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
