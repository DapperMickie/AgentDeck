using AgentDeck.Shared.Models;

namespace AgentDeck.Coordinator.Configuration;

public sealed class CoordinatorSecurityPolicyOptions
{
    public string PolicyVersion { get; set; } = "1";
    public bool AllowUpdateStaging { get; set; } = true;
    public bool RequireCoordinatorOriginForArtifacts { get; set; } = true;
    public bool RequireUpdateArtifactChecksum { get; set; } = true;
    public bool RequireSignedUpdateManifest { get; set; } = true;
    public bool RequireManifestProvenance { get; set; } = true;
    public IReadOnlyList<RunnerTrustedManifestSigner> TrustedManifestSigners { get; set; } = [];
    public bool AllowWorkflowPackExecution { get; set; }
    public bool AllowUpdateApply { get; set; }
}
