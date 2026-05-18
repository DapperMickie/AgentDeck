namespace AgentDeck.Shared.Models;

public sealed class RunnerOrchestratorCatalog
{
    public IReadOnlyList<RunnerOrchestratorProvider> Providers { get; init; } = [];
    public IReadOnlyList<RunnerOrchestratorTemplate> Templates { get; init; } = [];
    public IReadOnlyList<RunnerOrchestratorInstance> Instances { get; init; } = [];
}
