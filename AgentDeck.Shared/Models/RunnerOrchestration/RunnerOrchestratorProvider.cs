using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

public sealed class RunnerOrchestratorProvider
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public RunnerOrchestratorProviderKind Kind { get; init; } = RunnerOrchestratorProviderKind.Portainer;
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Configured { get; init; }
    public string? EndpointUrl { get; init; }
    public string? SecretReference { get; init; }
    public string? DefaultEnvironmentId { get; init; }
    public string? StatusMessage { get; init; }
    public RunnerOrchestratorProviderCapability Capabilities { get; init; } = new();
}
