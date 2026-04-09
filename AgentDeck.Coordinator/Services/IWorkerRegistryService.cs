using AgentDeck.Shared.Models;

namespace AgentDeck.Coordinator.Services;

public interface IWorkerRegistryService
{
    Task<IReadOnlyList<RegisteredRunnerMachine>> GetMachinesAsync(CancellationToken cancellationToken = default);

    RegisterRunnerMachineResponse RegisterOrUpdateWorker(RegisterRunnerMachineRequest request);
}
