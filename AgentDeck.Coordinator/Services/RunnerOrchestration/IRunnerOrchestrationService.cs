using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

namespace AgentDeck.Coordinator.Services;

public interface IRunnerOrchestrationService
{
    RunnerOrchestratorCatalog GetCatalog();
    RunnerOrchestratorProvider? GetProvider(string providerId);
    RunnerOrchestratorTemplate? GetTemplate(string templateId);
    RunnerOrchestratorInstance? GetInstance(string instanceId);
    Task<RunnerOrchestratorInstance> CreateInstanceAsync(CreateRunnerOrchestratorInstanceRequest request, CancellationToken cancellationToken = default);
    Task<RunnerOrchestratorInstance?> UpdateInstanceLifecycleAsync(string instanceId, RunnerInstanceLifecycleState requestedState, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RunnerOrchestratorInstanceEvent>> GetInstanceEventsAsync(string instanceId, CancellationToken cancellationToken = default);
}
