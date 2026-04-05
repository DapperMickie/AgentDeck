using AgentDeck.Core.Models;

namespace AgentDeck.Core.Services;

/// <summary>Loads built-in workloads and persists custom workloads for the companion app.</summary>
public interface IWorkloadCatalogService
{
    Task<IReadOnlyList<WorkloadDefinition>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkloadDefinition>> GetBuiltInAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkloadDefinition>> GetCustomAsync(CancellationToken cancellationToken = default);
    Task SaveCustomAsync(WorkloadDefinition workload, CancellationToken cancellationToken = default);
    Task DeleteCustomAsync(string workloadId, CancellationToken cancellationToken = default);
}
