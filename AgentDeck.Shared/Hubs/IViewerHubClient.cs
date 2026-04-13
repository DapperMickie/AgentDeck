using AgentDeck.Shared.Models;

namespace AgentDeck.Shared.Hubs;

public interface IViewerHubClient
{
    Task ViewerSessionUpdatedAsync(RemoteViewerSession session);
    Task ViewerFramePublishedAsync(RemoteViewerRelayFrame frame);
}
