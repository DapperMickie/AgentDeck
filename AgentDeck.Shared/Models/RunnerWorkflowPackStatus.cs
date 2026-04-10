using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Runner-reported status of a coordinator-assigned workflow pack.</summary>
public sealed class RunnerWorkflowPackStatus
{
    public RunnerWorkflowPackState State { get; init; } = RunnerWorkflowPackState.None;
    public bool ExecutionAttempted { get; init; }
    public string? PackId { get; init; }
    public string? PackVersion { get; init; }
    public string? LocalPackPath { get; init; }
    public DateTimeOffset? FetchedAt { get; init; }
    public string? StatusMessage { get; init; }
    public string? FailureMessage { get; init; }
}
