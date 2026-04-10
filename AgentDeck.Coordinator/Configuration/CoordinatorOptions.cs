namespace AgentDeck.Coordinator.Configuration;

public sealed class CoordinatorOptions
{
    public const string SectionName = "Coordinator";

    public int Port { get; set; } = 5001;

    public string? PublicBaseUrl { get; set; }

    public string ArtifactRoot { get; set; } = "artifacts";

    public string DesiredRunnerVersion { get; set; } = "0.1.0-dev";

    public int MinimumSupportedProtocolVersion { get; set; } = 1;

    public int MaximumSupportedProtocolVersion { get; set; } = 1;

    public string WorkflowCatalogVersion { get; set; } = "1";

    public CoordinatorUpdateManifestOptions DesiredUpdateManifest { get; set; } = new();

    public CoordinatorWorkflowPackOptions DesiredWorkflowPack { get; set; } = new();

    public CoordinatorCapabilityCatalogOptions DesiredCapabilityCatalog { get; set; } = new();

    public CoordinatorSecurityPolicyOptions SecurityPolicy { get; set; } = new();

    public bool ApplyStagedUpdate { get; set; }

    public TimeSpan WorkerHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan WorkerExpiry { get; set; } = TimeSpan.FromSeconds(45);
}
