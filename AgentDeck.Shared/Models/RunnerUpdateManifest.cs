namespace AgentDeck.Shared.Models;

/// <summary>Describes a versioned runner artifact that a coordinator can assign to workers.</summary>
public sealed class RunnerUpdateManifest
{
    public required string ManifestId { get; init; }
    public required string Version { get; init; }
    public string Channel { get; init; } = "stable";
    public required string ArtifactUrl { get; init; }
    public string? Sha256 { get; init; }
    public long? ArtifactSizeBytes { get; init; }
    public int MinimumProtocolVersion { get; init; } = 1;
    public int MaximumProtocolVersion { get; init; } = 1;
    public DateTimeOffset PublishedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? Notes { get; init; }
}
