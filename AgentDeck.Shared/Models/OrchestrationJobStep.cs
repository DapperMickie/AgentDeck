using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Represents a planned or active step in a coordinator-managed job.</summary>
public sealed class OrchestrationJobStep
{
    public string Name { get; init; } = string.Empty;
    public OrchestrationJobStepStatus Status { get; init; } = OrchestrationJobStepStatus.Pending;
    public string? Message { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}
