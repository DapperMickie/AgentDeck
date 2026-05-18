using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

public sealed class CreateRunnerOrchestratorInstanceRequest
{
    public string TemplateId { get; init; } = string.Empty;
    public string? Name { get; init; }
    public RunnerInstanceLifecyclePolicy LifecyclePolicy { get; init; } = RunnerInstanceLifecyclePolicy.Ephemeral;
    public string? CoordinatorUrl { get; init; }
    public string? ProjectId { get; init; }
    public string? ProjectName { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
}
