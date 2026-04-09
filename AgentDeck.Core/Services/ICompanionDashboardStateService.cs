using AgentDeck.Core.Models;

namespace AgentDeck.Core.Services;

public interface ICompanionDashboardStateService
{
    Task<CompanionDashboardState> BuildAsync(CancellationToken cancellationToken = default);
}
