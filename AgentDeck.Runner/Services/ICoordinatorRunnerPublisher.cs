using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

public interface ICoordinatorRunnerPublisher
{
    Task PublishTerminalOutputAsync(TerminalOutput output, CancellationToken cancellationToken = default);
    Task PublishTerminalSessionUpdatedAsync(TerminalSession session, CancellationToken cancellationToken = default);
    Task PublishTerminalSessionClosedAsync(string sessionId, CancellationToken cancellationToken = default);
    Task PublishViewerSessionUpdatedAsync(RemoteViewerSession session, CancellationToken cancellationToken = default);
    Task PublishViewerFrameAsync(RemoteViewerRelayFrame frame, CancellationToken cancellationToken = default);
}
