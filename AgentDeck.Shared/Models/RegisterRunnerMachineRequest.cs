using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Worker-to-coordinator registration payload.</summary>
public sealed class RegisterRunnerMachineRequest
{
    public required string MachineId { get; init; }
    public required string MachineName { get; init; }
    public RunnerMachineRole Role { get; init; } = RunnerMachineRole.Worker;
    public required string AgentVersion { get; init; }
    public int ProtocolVersion { get; init; } = 1;
    public string? WorkflowCatalogVersion { get; init; }
    public RunnerWorkflowCatalogStatus? WorkflowCatalogStatus { get; init; }
    public string? CapabilityCatalogVersion { get; init; }
    public RunnerCapabilityCatalogStatus? CapabilityCatalogStatus { get; init; }
    public string? SetupCatalogVersion { get; init; }
    public RunnerSetupCatalogStatus? SetupCatalogStatus { get; init; }
    public RunnerUpdateStatus? UpdateStatus { get; init; }
    public RunnerWorkflowPackStatus? WorkflowPackStatus { get; init; }
    public string? RunnerUrl { get; init; }
    public MachinePlatformProfile? Platform { get; init; }
    public IReadOnlyList<MachineTargetSupport> SupportedTargets { get; init; } = [];
    public IReadOnlyList<RemoteViewerProviderCapability> RemoteViewerProviders { get; init; } = [];
}
