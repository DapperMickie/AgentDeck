using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

public interface IRunnerUpdateStagingService
{
    Task<RunnerUpdateStatus?> GetCurrentStatusAsync(CancellationToken cancellationToken = default);

    Task<RunnerUpdateStatus?> ReconcileDesiredUpdateAsync(
        HttpClient coordinatorClient,
        RunnerDesiredState desiredState,
        CancellationToken cancellationToken = default);
}
