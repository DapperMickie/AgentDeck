namespace AgentDeck.Runner.Configuration;

/// <summary>Strongly-typed configuration for the AgentDeck runner service.</summary>
public sealed class RunnerOptions
{
    public const string SectionName = "Runner";
    public const string WorkspaceEnvironmentVariable = "AGENTDECK_WORKSPACE";
    public const string PortEnvironmentVariable = "AGENTDECK_PORT";
    public const string BindAddressEnvironmentVariable = "AGENTDECK_BIND_ADDRESS";
    public const string DefaultShellEnvironmentVariable = "AGENTDECK_DEFAULT_SHELL";

    /// <summary>Root directory under which new project directories are created. Defaults to ~/AgentDeck if not set.</summary>
    public string WorkspaceRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AgentDeck");

    /// <summary>Port the Kestrel server listens on. Default 5000.</summary>
    public int Port { get; set; } = 5000;

    /// <summary>Address the Kestrel server listens on. Defaults to loopback; use 0.0.0.0 or a LAN IP only when intentionally exposing the runner.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>Origins allowed for browser CORS. Empty denies browser origins; use ["*"] only for explicit development scenarios.</summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>Default shell command when no command is specified in CreateTerminalRequest. Auto-detected from OS if null.</summary>
    public string? DefaultShell { get; set; }

    public static void ApplyEnvironmentOverrides(RunnerOptions options)
    {
        var workspaceRoot = Environment.GetEnvironmentVariable(WorkspaceEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
            options.WorkspaceRoot = workspaceRoot;

        var portValue = Environment.GetEnvironmentVariable(PortEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(portValue))
        {
            if (!int.TryParse(portValue, out var port) || port is < 1 or > 65535)
            {
                throw new InvalidOperationException(
                    $"{PortEnvironmentVariable} must be an integer between 1 and 65535.");
            }

            options.Port = port;
        }

        var bindAddress = Environment.GetEnvironmentVariable(BindAddressEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(bindAddress))
            options.BindAddress = bindAddress.Trim();

        var defaultShell = Environment.GetEnvironmentVariable(DefaultShellEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(defaultShell))
            options.DefaultShell = defaultShell;
    }
}
