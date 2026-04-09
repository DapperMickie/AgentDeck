namespace AgentDeck.Coordinator.Configuration;

public sealed class CoordinatorUpdateManifestOptions
{
    public string ManifestId { get; set; } = "runner-stable";
    public string Version { get; set; } = "0.1.0-dev";
    public string Channel { get; set; } = "stable";
    public string ArtifactUrl { get; set; } = "https://example.invalid/agentdeck/runner-0.1.0-dev.zip";
    public string? Sha256 { get; set; }
    public long? ArtifactSizeBytes { get; set; }
    public int MinimumProtocolVersion { get; set; } = 1;
    public int MaximumProtocolVersion { get; set; } = 1;
    public string? Notes { get; set; }
}
