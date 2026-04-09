using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Declares whether a runner machine can host a specific project target.</summary>
public sealed class MachineTargetSupport
{
    public ApplicationTargetPlatform Platform { get; init; }
    public MachineTargetSupportStatus Status { get; init; } = MachineTargetSupportStatus.Unsupported;
    public string DisplayName { get; init; } = string.Empty;
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];
    public VirtualDeviceCatalogKind? DeviceCatalog { get; init; }
    public bool RequiresDeviceSelection { get; init; }
    public int AvailableDeviceCount { get; init; }
    public int AvailableDeviceProfileCount { get; init; }
    public string? Notes { get; init; }
}
