using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Describes whether a supported tool is available on a runner machine.</summary>
public sealed class MachineCapability
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required MachineCapabilityStatus Status { get; init; }
    public string? Version { get; init; }
    public IReadOnlyList<string> InstalledVersions { get; init; } = [];
    public string? Message { get; init; }
}
