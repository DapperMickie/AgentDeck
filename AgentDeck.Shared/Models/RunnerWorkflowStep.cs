using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Single step within a coordinator-defined runner workflow pack.</summary>
public sealed class RunnerWorkflowStep
{
    public required string StepId { get; init; }
    public RunnerWorkflowStepKind Kind { get; init; } = RunnerWorkflowStepKind.RunCommand;
    public string? DisplayName { get; init; }
    public string? CommandText { get; init; }
    public string? SourceUri { get; init; }
    public string? DestinationPath { get; init; }
    public IReadOnlyDictionary<string, string> Inputs { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
