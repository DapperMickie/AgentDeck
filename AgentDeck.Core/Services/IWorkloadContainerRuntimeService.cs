using AgentDeck.Core.Models;

namespace AgentDeck.Core.Services;

/// <summary>Executes local Docker lifecycle operations for workload-driven runner containers.</summary>
public interface IWorkloadContainerRuntimeService
{
    Task<WorkloadContainerStatus> GetStatusAsync(RunnerMachineSettings machine, WorkloadDefinition workload, CancellationToken cancellationToken = default);
    Task<WorkloadContainerExecutionResult> BuildBaseImageAsync(RunnerMachineSettings machine, WorkloadDefinition workload, CancellationToken cancellationToken = default);
    Task<WorkloadContainerExecutionResult> BuildWorkloadImageAsync(RunnerMachineSettings machine, WorkloadDefinition workload, CancellationToken cancellationToken = default);
    Task<WorkloadContainerExecutionResult> StartContainerAsync(RunnerMachineSettings machine, WorkloadDefinition workload, CancellationToken cancellationToken = default);
    Task<WorkloadContainerExecutionResult> StopContainerAsync(RunnerMachineSettings machine, WorkloadDefinition workload, CancellationToken cancellationToken = default);
}
