namespace AgentDeck.Shared.Enums;

/// <summary>Status for an individual orchestration step inside a job.</summary>
public enum OrchestrationJobStepStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped
}
