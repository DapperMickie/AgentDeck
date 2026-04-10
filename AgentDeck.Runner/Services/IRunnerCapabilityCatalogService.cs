using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

public interface IRunnerCapabilityCatalogService
{
    Task<RunnerCapabilityCatalog?> GetCurrentCatalogAsync(CancellationToken cancellationToken = default);

    Task<RunnerCapabilityCatalogStatus?> GetCurrentStatusAsync(CancellationToken cancellationToken = default);

    Task<RunnerCapabilityCatalogStatus> ReconcileDesiredCapabilityCatalogAsync(
        HttpClient coordinatorClient,
        RunnerDesiredState desiredState,
        CancellationToken cancellationToken = default);
}
