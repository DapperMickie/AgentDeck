using AgentDeck.Shared.Enums;

namespace AgentDeck.Coordinator.Configuration;

public sealed class CoordinatorRunnerOrchestrationOptions
{
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<CoordinatorRunnerOrchestratorProviderOptions> Providers { get; init; } = [];
    public IReadOnlyList<CoordinatorRunnerOrchestratorTemplateOptions> Templates { get; init; } = [];
}

public sealed class CoordinatorRunnerOrchestratorProviderOptions
{
    public string Id { get; init; } = "portainer-local";
    public string Name { get; init; } = "Portainer";
    public RunnerOrchestratorProviderKind Kind { get; init; } = RunnerOrchestratorProviderKind.Portainer;
    public bool Enabled { get; init; } = true;
    public string? Description { get; init; }
    public string? EndpointUrl { get; init; }
    public string? ApiToken { get; init; }
    public string? SecretReference { get; init; }
    public string? DefaultEnvironmentId { get; init; }
}

public sealed class CoordinatorRunnerOrchestratorTemplateOptions
{
    public string Id { get; init; } = "linux-container-default";
    public string ProviderId { get; init; } = "portainer-local";
    public string Name { get; init; } = "Linux container runner";
    public string? Description { get; init; }
    public RunnerHostPlatform HostPlatform { get; init; } = RunnerHostPlatform.Linux;
    public string Architecture { get; init; } = "amd64";
    public string Image { get; init; } = "docker.robsengaming.com/agentdeck-runner:latest";
    public string WorkspaceRoot { get; init; } = "/workspace";
    public string? WorkspaceVolume { get; init; } = "agentdeck-workspaces";
    public int RunnerPort { get; init; } = 5000;
    public string? NetworkName { get; init; }
    public bool ManagedViewerEnabled { get; init; } = true;
    public RunnerInstanceLifecyclePolicy DefaultLifecyclePolicy { get; init; } = RunnerInstanceLifecyclePolicy.Ephemeral;
    public TimeSpan? IdleTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan? MaxLifetime { get; init; } = TimeSpan.FromHours(8);
    public IReadOnlyList<string> CapabilityProfile { get; init; } = ["git", "node", "dotnet"];
}
