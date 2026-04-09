using AgentDeck.Shared.Models;

namespace AgentDeck.Core.Services;

/// <summary>Queries the central coordinator API over HTTP.</summary>
public interface ICoordinatorApiClient
{
    Task<bool> CheckHealthAsync(string coordinatorUrl, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RegisteredRunnerMachine>> GetMachinesAsync(string coordinatorUrl, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectDefinition>> GetProjectsAsync(string coordinatorUrl, CancellationToken cancellationToken = default);
}
