namespace AgentDeck.Shared.Models;

/// <summary>Result of resolving a launch-time device selection against discovered runner devices and known profiles.</summary>
public sealed class VirtualDeviceLaunchResolution
{
    public VirtualDeviceLaunchSelection Selection { get; init; } = new();
    public VirtualDeviceProfile? Profile { get; init; }
    public VirtualDeviceInstance? Device { get; init; }
    public bool CanLaunch { get; init; }
    public string? Message { get; init; }
}
