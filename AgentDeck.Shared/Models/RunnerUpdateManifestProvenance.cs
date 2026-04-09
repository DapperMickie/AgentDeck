namespace AgentDeck.Shared.Models;

/// <summary>Describes where a runner update artifact came from.</summary>
public sealed class RunnerUpdateManifestProvenance
{
    public required string SourceRepository { get; init; }
    public required string SourceRevision { get; init; }
    public string? BuildIdentifier { get; init; }
    public string? PublishedBy { get; init; }
    public string? ProvenanceUri { get; init; }
}
