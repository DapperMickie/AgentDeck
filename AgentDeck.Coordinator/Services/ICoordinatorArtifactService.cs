namespace AgentDeck.Coordinator.Services;

public interface ICoordinatorArtifactService
{
    CoordinatorHostedArtifact ResolveHostedArtifact(string relativePath, string? publicBaseUrl);

    string? TryResolveArtifactPath(string relativePath);
}
