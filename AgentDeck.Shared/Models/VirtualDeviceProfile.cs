using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Selectable emulator or simulator profile definition.</summary>
public sealed class VirtualDeviceProfile
{
    public string Id { get; init; } = string.Empty;
    public VirtualDeviceCatalogKind CatalogKind { get; init; }
    public ApplicationTargetPlatform TargetPlatform { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public VirtualDeviceFormFactor FormFactor { get; init; }
    public string PlatformVersion { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public bool IsDefault { get; init; }
    public string? Notes { get; init; }
}
