namespace AgentDeck.Shared.Enums;

/// <summary>Lifecycle state for a runner instance created by an orchestrator provider.</summary>
public enum RunnerInstanceLifecycleState
{
    Creating,
    Booting,
    Registering,
    Ready,
    Busy,
    Idle,
    Draining,
    Stopped,
    Failed,
    Deleting,
    Deleted
}
