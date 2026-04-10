namespace AgentDeck.Shared.Enums;

/// <summary>Controls how the coordinator should request runner update apply for a specific machine.</summary>
public enum MachineUpdateApplyIntentMode
{
    Inherit,
    RequestApply,
    StageOnly
}
