using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

public sealed class RunnerCapabilityCatalog
{
    public required string CatalogId { get; init; }
    public required string Version { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<RunnerCapabilityDefinition> Capabilities { get; init; } = [];
}

public sealed class RunnerCapabilityDefinition
{
    public required string CapabilityId { get; init; }
    public required string DisplayName { get; init; }
    public required string Category { get; init; }
    public RunnerCapabilityProbeKind ProbeKind { get; init; }
    public IReadOnlyList<RunnerCapabilityProbeCommand> ProbeCommands { get; init; } = [];
}

public sealed class RunnerCapabilityProbeCommand
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
}
