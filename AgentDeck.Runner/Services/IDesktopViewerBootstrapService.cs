using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

/// <summary>Bootstraps and tears down first-pass viewer transports.</summary>
public interface IDesktopViewerBootstrapService
{
    Task<RemoteViewerSession?> BootstrapAsync(string sessionId, string? connectionHost = null, CancellationToken cancellationToken = default);
    Task<RemoteViewerSessionMutationResult> CloseAsync(string sessionId, string? message = null, CancellationToken cancellationToken = default);
}
