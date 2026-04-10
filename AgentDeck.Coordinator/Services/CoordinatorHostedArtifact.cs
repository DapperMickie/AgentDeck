namespace AgentDeck.Coordinator.Services;

public sealed record CoordinatorHostedArtifact(
    string RelativePath,
    string PhysicalPath,
    string DownloadUrl,
    string Sha256,
    long ArtifactSizeBytes);
