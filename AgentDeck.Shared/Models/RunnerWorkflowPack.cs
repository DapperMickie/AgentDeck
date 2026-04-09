namespace AgentDeck.Shared.Models;

/// <summary>Coordinator-published workflow pack for runner-side execution primitives.</summary>
public sealed class RunnerWorkflowPack
{
    public required string PackId { get; init; }
    public required string Version { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<RunnerWorkflowStep> Steps { get; init; } = [];
}
