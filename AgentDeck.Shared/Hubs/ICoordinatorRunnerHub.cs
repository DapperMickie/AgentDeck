using AgentDeck.Shared.Models;

namespace AgentDeck.Shared.Hubs;

public interface ICoordinatorRunnerHub
{
    Task PublishTerminalOutputAsync(TerminalOutput output);
    Task PublishTerminalSessionUpdatedAsync(TerminalSession session);
    Task PublishTerminalSessionClosedAsync(string sessionId);
    Task PublishOrchestrationJobUpdatedAsync(OrchestrationJob job);
    Task PublishViewerSessionUpdatedAsync(RemoteViewerSession session);
    Task PublishViewerFrameAsync(RemoteViewerRelayFrame frame);
}
