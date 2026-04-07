using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Basic host-platform metadata reported by a runner machine.</summary>
public sealed class MachinePlatformProfile
{
    public RunnerHostPlatform HostPlatform { get; init; } = RunnerHostPlatform.Unknown;
    public string? OperatingSystemDescription { get; init; }
    public string? Architecture { get; init; }
}
