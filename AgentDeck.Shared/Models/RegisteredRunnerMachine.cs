using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Coordinator-visible machine record for local and worker runners.</summary>
public sealed class RegisteredRunnerMachine
{
    public required string MachineId { get; init; }
    public required string MachineName { get; init; }
    public RunnerMachineRole Role { get; init; } = RunnerMachineRole.Standalone;
    public required string AgentVersion { get; init; }
    public int ProtocolVersion { get; init; } = 1;
    public string? WorkflowCatalogVersion { get; init; }
    public RunnerWorkflowCatalogStatus? WorkflowCatalogStatus { get; init; }
    public string? SecurityPolicyVersion { get; init; }
    public string? DesiredUpdateManifestId { get; init; }
    public string? DesiredWorkflowPackId { get; init; }
    public RunnerUpdateStatus? UpdateStatus { get; init; }
    public RunnerUpdateRolloutStatus? UpdateRollout { get; init; }
    public RunnerWorkflowPackStatus? WorkflowPackStatus { get; init; }
    public string? RunnerUrl { get; init; }
    public DateTimeOffset RegisteredAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; init; } = DateTimeOffset.UtcNow;
    public bool IsOnline { get; init; } = true;
    public bool IsCoordinator { get; init; }
    public bool UpdateAvailable { get; init; }
    public bool ProtocolCompatible { get; init; } = true;
    public MachinePlatformProfile? Platform { get; init; }
    public IReadOnlyList<MachineTargetSupport> SupportedTargets { get; init; } = [];
    public IReadOnlyList<RemoteViewerProviderCapability> RemoteViewerProviders { get; init; } = [];
}
