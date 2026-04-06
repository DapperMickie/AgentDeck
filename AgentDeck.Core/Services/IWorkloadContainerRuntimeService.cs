using AgentDeck.Core.Models;

namespace AgentDeck.Core.Services;

/// <summary>Executes local Docker lifecycle operations for workload-driven runner containers.</summary>
public interface IWorkloadContainerRuntimeService
{
    Task<WorkloadContainerStatus> GetStatusAsync(ConnectionSettings settings, WorkloadDefinition workload, CancellationToken cancellationToken = default);
    Task<WorkloadContainerExecutionResult> BuildBaseImageAsync(ConnectionSettings settings, WorkloadDefinition workload, CancellationToken cancellationToken = default);
    Task<WorkloadContainerExecutionResult> BuildWorkloadImageAsync(ConnectionSettings settings, WorkloadDefinition workload, CancellationToken cancellationToken = default);
    Task<WorkloadContainerExecutionResult> StartContainerAsync(ConnectionSettings settings, WorkloadDefinition workload, CancellationToken cancellationToken = default);
    Task<WorkloadContainerExecutionResult> StopContainerAsync(ConnectionSettings settings, WorkloadDefinition workload, CancellationToken cancellationToken = default);
}
