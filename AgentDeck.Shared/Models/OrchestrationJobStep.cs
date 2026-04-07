using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Represents a planned or active step in a coordinator-managed job.</summary>
public sealed class OrchestrationJobStep
{
    public string Name { get; init; } = string.Empty;
    public OrchestrationJobStepStatus Status { get; set; } = OrchestrationJobStepStatus.Pending;
    public string? Message { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
