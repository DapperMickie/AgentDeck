namespace AgentDeck.Shared.Enums;

/// <summary>Lifecycle state for a coordinator-managed run or debug job.</summary>
public enum OrchestrationJobStatus
{
    Queued,
    Preparing,
    Dispatching,
    Running,
    Completed,
    Failed,
    CancelRequested,
    Cancelled
}
