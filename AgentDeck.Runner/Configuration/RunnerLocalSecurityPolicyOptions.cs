namespace AgentDeck.Runner.Configuration;

/// <summary>Runner-local minimum security policy. Coordinator payloads may only add stricter requirements.</summary>
public sealed class RunnerLocalSecurityPolicyOptions
{
    public const string SectionName = "RunnerLocalSecurityPolicy";

    public bool RequireCoordinatorOriginForArtifacts { get; set; } = true;
    public bool RequireUpdateArtifactChecksum { get; set; } = true;
    public bool RequireSignedUpdateManifest { get; set; } = true;
    public bool RequireManifestProvenance { get; set; } = true;
    public bool RequireSignedWorkflowPacks { get; set; } = true;
    public bool RequireSignedCapabilityCatalogs { get; set; } = true;
    public bool RequireSignedSetupCatalogs { get; set; } = true;
    public bool AllowDevSignerInProduction { get; set; }
    public IReadOnlyList<string> TrustedSignerIds { get; set; } = [];
    public string? TrustedSignersDirectory { get; set; }
}
