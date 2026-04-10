using AgentDeck.Shared.Models;

namespace AgentDeck.Coordinator.Services;

public interface IWorkerRegistryService
{
    Task<IReadOnlyList<RegisteredRunnerMachine>> GetMachinesAsync(CancellationToken cancellationToken = default);

    Task<RegisteredRunnerMachine?> GetMachineAsync(string machineId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RunnerUpdateRolloutStatus>> GetUpdateRolloutsAsync(CancellationToken cancellationToken = default);

    Task<RunnerUpdateRolloutStatus?> GetUpdateRolloutAsync(string machineId, CancellationToken cancellationToken = default);

    RegisterRunnerMachineResponse RegisterOrUpdateWorker(RegisterRunnerMachineRequest request);
}
