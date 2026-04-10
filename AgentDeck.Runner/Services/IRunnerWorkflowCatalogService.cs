using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

public interface IRunnerWorkflowCatalogService
{
    Task<RunnerWorkflowCatalogStatus?> GetCurrentStatusAsync(CancellationToken cancellationToken = default);

    Task<RunnerWorkflowCatalogStatus> ReconcileDesiredWorkflowCatalogAsync(
        RunnerDesiredState desiredState,
        CancellationToken cancellationToken = default);
}
