using AgentDeck.Shared.Models;
using AgentDeck.Shared.Protocol;

namespace AgentDeck.Shared.Hubs;

public interface ICoordinatorViewerHub
{
    Task<HubProtocolHelloAck> HelloAsync(HubProtocolHello hello);

    Task JoinViewerSessionAsync(string machineId, string viewerSessionId);
    Task LeaveViewerSessionAsync(string viewerSessionId);
    Task SendViewerPointerInputAsync(string machineId, string viewerSessionId, RemoteViewerPointerInputEvent input);
    Task SendViewerKeyboardInputAsync(string machineId, string viewerSessionId, RemoteViewerKeyboardInputEvent input);
}
