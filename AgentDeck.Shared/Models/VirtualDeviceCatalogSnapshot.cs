using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Catalog of supported device profiles and discovered instances for one provider family.</summary>
public sealed class VirtualDeviceCatalogSnapshot
{
    public VirtualDeviceCatalogKind CatalogKind { get; init; }
    public RunnerHostPlatform HostPlatform { get; init; } = RunnerHostPlatform.Unknown;
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<VirtualDeviceProfile> Profiles { get; init; } = [];
    public IReadOnlyList<VirtualDeviceInstance> Devices { get; init; } = [];
}
