using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

public interface ITerminalSessionService
{
    Task<TerminalSession> CreateSessionAsync(CreateTerminalRequest request, CancellationToken cancellationToken = default);
}
