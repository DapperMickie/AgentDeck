using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Coordinator-facing summary of runner update rollout and apply state.</summary>
public sealed class RunnerUpdateRolloutStatus
{
    public required string MachineId { get; init; }
    public required string MachineName { get; init; }
    public required string CurrentVersion { get; init; }
    public required string DesiredVersion { get; init; }
    public string? DesiredManifestId { get; init; }
    public string? DesiredManifestVersion { get; init; }
    public string? SecurityPolicyVersion { get; init; }
    public RunnerUpdateRolloutState State { get; init; } = RunnerUpdateRolloutState.UpToDate;
    public RunnerUpdateStageState? RunnerStageState { get; init; }
    public bool IsOnline { get; init; } = true;
    public bool UpdateAvailable { get; init; }
    public bool ApplyRequested { get; init; }
    public bool ApplyPermitted { get; init; }
    public bool ApplyEligible { get; init; }
    public string? StatusMessage { get; init; }
    public string? BlockingReason { get; init; }
    public string? FailureMessage { get; init; }
    public DateTimeOffset? StagedAt { get; init; }
    public DateTimeOffset? ApplyStartedAt { get; init; }
    public DateTimeOffset? AppliedAt { get; init; }
}
