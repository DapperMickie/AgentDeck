using AgentDeck.Shared.Enums;

namespace AgentDeck.Coordinator.Configuration;

public sealed class CoordinatorWorkflowStepOptions
{
    public string StepId { get; set; } = "step-1";
    public RunnerWorkflowStepKind Kind { get; set; } = RunnerWorkflowStepKind.RunCommand;
    public string? DisplayName { get; set; }
    public string? CommandText { get; set; }
    public string? SourceUri { get; set; }
    public string? DestinationPath { get; set; }
    public Dictionary<string, string> Inputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
