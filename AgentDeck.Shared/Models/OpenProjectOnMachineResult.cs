namespace AgentDeck.Shared.Models;

/// <summary>Coordinator response for opening a project on a specific machine.</summary>
public sealed class OpenProjectOnMachineResult
{
    public ProjectDefinition Project { get; init; } = new();
    public ProjectSessionRecord ProjectSession { get; init; } = new();
    public ProjectWorkspaceMapping Workspace { get; init; } = new();
    public required TerminalSession Session { get; init; }
    public bool BootstrapPending { get; init; }
    public string? BootstrapMessage { get; init; }
    public bool WorkspaceCreated { get; init; }
    public bool RepositoryCloned { get; init; }
}
