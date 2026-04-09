namespace AgentDeck.Shared.Models;

/// <summary>Coordinator-declared security policy for runner control-plane behavior.</summary>
public sealed class RunnerControlPlaneSecurityPolicy
{
    public string PolicyVersion { get; init; } = "1";
    public bool AllowUpdateStaging { get; init; } = true;
    public bool RequireCoordinatorOriginForArtifacts { get; init; } = true;
    public bool RequireUpdateArtifactChecksum { get; init; } = true;
    public bool AllowWorkflowPackExecution { get; init; }
    public bool AllowUpdateApply { get; init; }
}
