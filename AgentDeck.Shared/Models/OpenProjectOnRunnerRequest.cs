namespace AgentDeck.Shared.Models;

/// <summary>Coordinator-to-runner request for opening or bootstrapping a project workspace.</summary>
public sealed class OpenProjectOnRunnerRequest
{
    public string ProjectId { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public ProjectRepositoryReference Repository { get; init; } = new();
    public string? ExistingWorkspacePath { get; init; }
}
