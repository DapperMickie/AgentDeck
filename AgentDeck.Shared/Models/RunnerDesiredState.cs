namespace AgentDeck.Shared.Models;

/// <summary>Coordinator-declared desired state for a runner agent.</summary>
public sealed class RunnerDesiredState
{
    public int MinimumSupportedProtocolVersion { get; init; } = 1;
    public int MaximumSupportedProtocolVersion { get; init; } = 1;
    public required string DesiredRunnerVersion { get; init; }
    public required RunnerControlPlaneSecurityPolicy SecurityPolicy { get; init; }
    public RunnerDefinitionReference? DesiredUpdateManifest { get; init; }
    public RunnerDefinitionReference? DesiredWorkflowPack { get; init; }
    public RunnerDefinitionReference? DesiredCapabilityCatalog { get; init; }
    public RunnerDefinitionReference? DesiredSetupCatalog { get; init; }
    public string? WorkflowCatalogVersion { get; init; }
    public string? CapabilityCatalogVersion { get; init; }
    public string? SetupCatalogVersion { get; init; }
    public bool UpdateAvailable { get; init; }
    public bool ApplyUpdate { get; init; }
    public bool ProtocolCompatible { get; init; } = true;
    public string? StatusMessage { get; init; }
}
