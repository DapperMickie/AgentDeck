namespace AgentDeck.Shared.Enums;

/// <summary>Readiness state for running a specific application target on a machine.</summary>
public enum MachineTargetSupportStatus
{
    Unsupported,
    RequiresSetup,
    Supported
}
