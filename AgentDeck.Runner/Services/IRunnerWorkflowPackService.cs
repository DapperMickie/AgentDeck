using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

public interface IRunnerWorkflowPackService
{
    Task<RunnerWorkflowPackStatus?> GetCurrentStatusAsync(CancellationToken cancellationToken = default);

    Task<RunnerWorkflowPackStatus?> ReconcileDesiredWorkflowPackAsync(
        HttpClient coordinatorClient,
        RunnerDesiredState desiredState,
        CancellationToken cancellationToken = default);
}
