namespace AgentDeck.Coordinator.Configuration;

public sealed class CoordinatorOptions
{
    public const string SectionName = "Coordinator";
    public const string PortEnvironmentVariable = "AGENTDECK_COORDINATOR_PORT";
    public const string BindAddressEnvironmentVariable = "AGENTDECK_COORDINATOR_BIND_ADDRESS";

    public int Port { get; set; } = 5001;

    public string BindAddress { get; set; } = "127.0.0.1";

    public string? AccessKey { get; set; }

    public string? PublicBaseUrl { get; set; }

    public string ArtifactRoot { get; set; } = "artifacts";

    public string DesiredRunnerVersion { get; set; } = "0.1.0-dev";

    public int MinimumSupportedProtocolVersion { get; set; } = 1;

    public int MaximumSupportedProtocolVersion { get; set; } = 1;

    public string WorkflowCatalogVersion { get; set; } = "1";

    public CoordinatorUpdateManifestOptions DesiredUpdateManifest { get; set; } = new();

    public CoordinatorWorkflowPackOptions DesiredWorkflowPack { get; set; } = new();

    public CoordinatorCapabilityCatalogOptions DesiredCapabilityCatalog { get; set; } = new();

    public CoordinatorSetupCatalogOptions DesiredSetupCatalog { get; set; } = new();

    public CoordinatorSecurityPolicyOptions SecurityPolicy { get; set; } = new();

    public bool ApplyStagedUpdate { get; set; }

    public TimeSpan WorkerHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan WorkerExpiry { get; set; } = TimeSpan.FromSeconds(45);

    public CoordinatorRunnerOrchestrationOptions RunnerOrchestration { get; set; } = new();

    public TimeSpan RunnerControlKeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan RunnerControlClientTimeoutInterval { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan RunnerControlHandshakeTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public long RunnerControlMaximumReceiveMessageSize { get; set; } = 1024 * 1024;

    public static void ApplyEnvironmentOverrides(CoordinatorOptions options)
    {
        var portValue = Environment.GetEnvironmentVariable(PortEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(portValue))
        {
            if (!int.TryParse(portValue, out var port) || port is < 1 or > 65535)
            {
                throw new InvalidOperationException(
                    $"{PortEnvironmentVariable} must be an integer between 1 and 65535.");
            }

            options.Port = port;
        }

        var bindAddress = Environment.GetEnvironmentVariable(BindAddressEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(bindAddress))
            options.BindAddress = bindAddress.Trim();

        var accessKey = Environment.GetEnvironmentVariable("AGENTDECK_COORDINATOR_ACCESS_KEY");
        if (!string.IsNullOrWhiteSpace(accessKey))
            options.AccessKey = accessKey.Trim();
    }
}
