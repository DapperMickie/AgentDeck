using AgentDeck.Shared.Models;

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
    public DateTimeOffset? PublishedAt { get; set; }
    public string? Notes { get; set; }
    public string? SourceRepository { get; set; }
    public string? SourceRevision { get; set; }
    public string? BuildIdentifier { get; set; }
    public string? PublishedBy { get; set; }
    public string? ProvenanceUri { get; set; }
    public string? SignerId { get; set; }
    public string SignatureAlgorithm { get; set; } = RunnerUpdateManifestSigning.RsaSha256Algorithm;
    public string? Signature { get; set; }
}
