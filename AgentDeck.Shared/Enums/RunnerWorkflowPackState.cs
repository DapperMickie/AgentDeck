namespace AgentDeck.Shared.Enums;

/// <summary>Runner-side reconciliation state for a coordinator-assigned workflow pack.</summary>
public enum RunnerWorkflowPackState
{
    None,
    Ready,
    Blocked,
    Failed
}
