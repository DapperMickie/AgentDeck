namespace AgentDeck.Shared.Models;

/// <summary>Runner response describing the resolved project workspace.</summary>
public sealed class OpenProjectOnRunnerResult
{
    public string ProjectPath { get; init; } = string.Empty;
    public string TerminalWorkingDirectory { get; init; } = string.Empty;
    public string? TerminalCommand { get; init; }
    public IReadOnlyList<string> TerminalArguments { get; init; } = [];
    public bool BootstrapPending { get; init; }
    public string? BootstrapMessage { get; init; }
    public bool WorkspaceCreated { get; init; }
    public bool RepositoryCloned { get; init; }
}
