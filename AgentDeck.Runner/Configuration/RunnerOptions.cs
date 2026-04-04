namespace AgentDeck.Runner.Configuration;

/// <summary>Strongly-typed configuration for the AgentDeck runner service.</summary>
public sealed class RunnerOptions
{
    public const string SectionName = "Runner";

    /// <summary>Root directory under which new project directories are created. Defaults to ~/AgentDeck if not set.</summary>
    public string WorkspaceRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AgentDeck");

    /// <summary>Port the Kestrel server listens on. Default 5000.</summary>
    public int Port { get; set; } = 5000;

    /// <summary>Origins allowed for CORS. Use ["*"] for development.</summary>
    public string[] AllowedOrigins { get; set; } = ["*"];

    /// <summary>Default shell command when no command is specified in CreateTerminalRequest. Auto-detected from OS if null.</summary>
    public string? DefaultShell { get; set; }
}
