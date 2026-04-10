namespace AgentDeck.Runner.Configuration;

/// <summary>Configures runner-owned remote-viewing transport launch behavior.</summary>
public sealed class DesktopViewerTransportOptions
{
    public const string SectionName = "DesktopViewerTransport";

    public ManagedDesktopViewerTransportOptions Managed { get; set; } = new();
}

/// <summary>Configures the first AgentDeck-managed viewer transport helper.</summary>
public sealed class ManagedDesktopViewerTransportOptions
{
    public bool Enabled { get; set; }
    public string? Command { get; set; }
    public string[] Arguments { get; set; } = [];
    public string? WorkingDirectory { get; set; }
    public string? ConnectionUriTemplate { get; set; }
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public bool IssueAccessToken { get; set; } = true;
    public int AccessTokenBytes { get; set; } = 12;
    public Dictionary<string, string> EnvironmentVariables { get; set; } = [];

    public bool IsConfigured =>
        Enabled &&
        !string.IsNullOrWhiteSpace(Command) &&
        !string.IsNullOrWhiteSpace(ConnectionUriTemplate);
}
