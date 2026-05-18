namespace AgentDeck.Shared.Enums;

/// <summary>How AgentDeck should treat created runner lifetime.</summary>
public enum RunnerInstanceLifecyclePolicy
{
    Ephemeral,
    Reusable,
    Persistent
}
