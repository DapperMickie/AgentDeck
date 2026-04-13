namespace AgentDeck.Runner.Configuration;

/// <summary>Configures runner-owned remote-viewing transport launch behavior.</summary>
public sealed class DesktopViewerTransportOptions
{
    public const string SectionName = "DesktopViewerTransport";

    public ManagedDesktopViewerTransportOptions Managed { get; set; } = new();
    public bool AllowNativeFallbackProviders { get; set; }
}

/// <summary>Configures the first AgentDeck-managed viewer transport helper.</summary>
public sealed class ManagedDesktopViewerTransportOptions
{
    public bool Enabled { get; set; }
    public bool UseEmbeddedRelay { get; set; } = true;
    public string? Command { get; set; }
    public string[] Arguments { get; set; } = [];
    public string? WorkingDirectory { get; set; }
    public string? ConnectionUriTemplate { get; set; }
    public string? ReadySignalPathTemplate { get; set; }
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan FrameInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    public string RelayHubPath { get; set; } = "/hubs/managed-viewer";
    public bool IssueAccessToken { get; set; } = true;
    public int AccessTokenBytes { get; set; } = 12;
    public Dictionary<string, string> EnvironmentVariables { get; set; } = [];

    public bool IsConfigured =>
        Enabled &&
        (UseEmbeddedRelay ||
         (!string.IsNullOrWhiteSpace(Command) &&
          !string.IsNullOrWhiteSpace(ConnectionUriTemplate)));
}
