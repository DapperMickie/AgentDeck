using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

public interface IRunnerSetupCatalogService
{
    Task<RunnerSetupCatalog?> GetCurrentCatalogAsync(CancellationToken cancellationToken = default);

    Task<RunnerSetupCatalogStatus?> GetCurrentStatusAsync(CancellationToken cancellationToken = default);

    Task<RunnerSetupCatalogStatus> ReconcileDesiredSetupCatalogAsync(
        HttpClient coordinatorClient,
        RunnerDesiredState desiredState,
        CancellationToken cancellationToken = default);
}
