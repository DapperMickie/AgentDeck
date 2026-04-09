namespace AgentDeck.Shared.Models;

/// <summary>Runner response describing the resolved project workspace.</summary>
public sealed class OpenProjectOnRunnerResult
{
    public string ProjectPath { get; init; } = string.Empty;
    public bool WorkspaceCreated { get; init; }
    public bool RepositoryCloned { get; init; }
}
