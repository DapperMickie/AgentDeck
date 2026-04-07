using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Launch-time device selection for emulator-backed and simulator-backed targets.</summary>
public sealed class VirtualDeviceLaunchSelection
{
    public VirtualDeviceCatalogKind CatalogKind { get; init; }
    public ApplicationTargetPlatform TargetPlatform { get; init; }
    public string? DeviceId { get; init; }
    public string? ProfileId { get; init; }
    public string? DisplayName { get; init; }
    public bool StartBeforeLaunch { get; init; } = true;
    public bool ReuseRunningDevice { get; init; } = true;

    /// <summary>True when the selection names a concrete device instance or a device profile to launch.</summary>
    public bool HasTarget => !string.IsNullOrWhiteSpace(DeviceId) || !string.IsNullOrWhiteSpace(ProfileId);
}
