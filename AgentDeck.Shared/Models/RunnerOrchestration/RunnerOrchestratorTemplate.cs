using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

public sealed class RunnerOrchestratorTemplate
{
    public string Id { get; init; } = string.Empty;
    public string ProviderId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public RunnerHostPlatform HostPlatform { get; init; } = RunnerHostPlatform.Linux;
    public string Architecture { get; init; } = "amd64";
    public string Image { get; init; } = "ghcr.io/dappermickie/agentdeck-runner:latest";
    public string WorkspaceRoot { get; init; } = "/workspace";
    public string? WorkspaceVolume { get; init; }
    public int? CpuLimit { get; init; }
    public int? MemoryMb { get; init; }
    public string? NetworkName { get; init; }
    public int RunnerPort { get; init; } = 5000;
    public bool ManagedViewerEnabled { get; init; } = true;
    public RunnerInstanceLifecyclePolicy DefaultLifecyclePolicy { get; init; } = RunnerInstanceLifecyclePolicy.Ephemeral;
    public TimeSpan? IdleTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan? MaxLifetime { get; init; } = TimeSpan.FromHours(8);
    public IReadOnlyList<string> CapabilityProfile { get; init; } = ["git", "node", "dotnet"];
}
