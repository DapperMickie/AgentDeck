using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Observed emulator or simulator instance discovered on a runner machine.</summary>
public sealed class VirtualDeviceInstance
{
    public string Id { get; init; } = string.Empty;
    public string ProfileId { get; init; } = string.Empty;
    public VirtualDeviceCatalogKind CatalogKind { get; init; }
    public ApplicationTargetPlatform TargetPlatform { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public VirtualDeviceState State { get; init; } = VirtualDeviceState.Unknown;
    public bool IsAvailableForLaunch { get; init; }
    public string? MachineId { get; init; }
    public string? MachineName { get; init; }
    public string? Notes { get; init; }
}
