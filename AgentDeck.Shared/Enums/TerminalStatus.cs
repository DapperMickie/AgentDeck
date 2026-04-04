namespace AgentDeck.Shared.Enums;

/// <summary>The lifecycle state of a terminal session.</summary>
public enum TerminalStatus
{
    /// <summary>The terminal process is running.</summary>
    Running,

    /// <summary>The terminal process has exited cleanly.</summary>
    Stopped,

    /// <summary>The terminal process exited with an error or crashed.</summary>
    Error
}
