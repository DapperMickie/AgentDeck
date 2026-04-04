namespace AgentDeck.Shared.Models;

/// <summary>Request payload for creating a new terminal session.</summary>
public sealed class CreateTerminalRequest
{
    /// <summary>Human-readable display name for the session.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Working directory for the new process. Must be a valid path;
    /// if relative, it is resolved against the runner's workspace root.
    /// </summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// Optional command override. When null the runner uses its configured
    /// default shell (e.g. powershell.exe on Windows, /bin/bash on Linux).
    /// </summary>
    public string? Command { get; init; }

    /// <summary>
    /// Optional arguments to pass to the command.
    /// Using a list avoids shell-escaping issues when launching CLIs with arguments.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Initial terminal width in columns.</summary>
    public int Cols { get; init; } = 80;

    /// <summary>Initial terminal height in rows.</summary>
    public int Rows { get; init; } = 24;
}
