using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Shared contract describing a supported project target.</summary>
public sealed class ProjectTargetDefinition
{
    public ProjectWorkloadKind Workload { get; init; }
    public ApplicationTargetPlatform Platform { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? CapabilityId { get; init; }
    public bool RequiresVsCodeForDebugging { get; init; }
    public string? PackageRequirement { get; init; }
    public string? Notes { get; init; }
}
