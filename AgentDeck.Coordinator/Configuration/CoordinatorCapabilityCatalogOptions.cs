using AgentDeck.Shared.Enums;

namespace AgentDeck.Coordinator.Configuration;

public sealed class CoordinatorCapabilityCatalogOptions
{
    public string CatalogId { get; set; } = "default";
    public string Version { get; set; } = "1";
    public string DisplayName { get; set; } = "Default capability catalog";
    public string? Description { get; set; }
    public IReadOnlyList<CoordinatorCapabilityDefinitionOptions> Capabilities { get; set; } = [];
}

public sealed class CoordinatorCapabilityDefinitionOptions
{
    public string CapabilityId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Category { get; set; } = "cli";
    public RunnerCapabilityProbeKind ProbeKind { get; set; } = RunnerCapabilityProbeKind.GenericCliVersion;
    public IReadOnlyList<CoordinatorCapabilityProbeCommandOptions> ProbeCommands { get; set; } = [];
}

public sealed class CoordinatorCapabilityProbeCommandOptions
{
    public string FileName { get; set; } = string.Empty;
    public IReadOnlyList<string> Arguments { get; set; } = [];
}
