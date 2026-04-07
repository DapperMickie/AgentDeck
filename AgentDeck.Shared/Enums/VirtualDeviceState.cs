namespace AgentDeck.Shared.Enums;

/// <summary>Observed lifecycle state for a discovered emulator or simulator instance.</summary>
public enum VirtualDeviceState
{
    Unknown,
    Available,
    Booting,
    Running,
    Busy,
    Unavailable
}
