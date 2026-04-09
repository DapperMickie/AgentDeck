using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

public interface IProjectWorkspaceBootstrapService
{
    Task<OpenProjectOnRunnerResult> OpenProjectAsync(OpenProjectOnRunnerRequest request, CancellationToken cancellationToken = default);
}
