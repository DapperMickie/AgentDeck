namespace AgentDeck.Shared.Models;

/// <summary>References a coordinator-published runner definition by identifier and version.</summary>
public sealed class RunnerDefinitionReference
{
    public required string DefinitionId { get; init; }
    public required string Version { get; init; }
}
