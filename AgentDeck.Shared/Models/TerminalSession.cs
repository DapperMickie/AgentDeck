using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Represents a managed terminal session on the runner.</summary>
public sealed class TerminalSession
{
    /// <summary>Unique session identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable display name.</summary>
    public required string Name { get; init; }

    /// <summary>Absolute working directory path used when the process was spawned.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>The command (executable) that was launched.</summary>
    public required string Command { get; init; }

    /// <summary>Arguments passed to the command when the process was spawned.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Current lifecycle status of the terminal session.</summary>
    public TerminalStatus Status { get; set; } = TerminalStatus.Running;

    /// <summary>
    /// The process exit code. Null while the session is still running;
    /// set when status transitions to <see cref="TerminalStatus.Stopped"/> or <see cref="TerminalStatus.Error"/>.
    /// </summary>
    public int? ExitCode { get; set; }

    /// <summary>UTC timestamp when the session was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
