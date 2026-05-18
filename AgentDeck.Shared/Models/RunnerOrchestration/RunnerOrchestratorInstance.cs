using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

public sealed class RunnerOrchestratorInstance
{
    public string Id { get; init; } = string.Empty;
    public string ProviderId { get; init; } = string.Empty;
    public string TemplateId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public RunnerInstanceLifecycleState State { get; init; } = RunnerInstanceLifecycleState.Creating;
    public RunnerInstanceLifecyclePolicy LifecyclePolicy { get; init; } = RunnerInstanceLifecyclePolicy.Ephemeral;
    public RunnerHostPlatform HostPlatform { get; init; } = RunnerHostPlatform.Linux;
    public string? MachineId { get; init; }
    public string? ProviderResourceId { get; init; }
    public string? EndpointUrl { get; init; }
    public string? StatusMessage { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; init; }
    public IReadOnlyList<RunnerOrchestratorInstanceEvent> Events { get; init; } = [];
}
