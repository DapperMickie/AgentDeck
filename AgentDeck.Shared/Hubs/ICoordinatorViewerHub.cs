using AgentDeck.Shared.Models;

namespace AgentDeck.Shared.Hubs;

public interface ICoordinatorViewerHub
{
    Task JoinViewerSessionAsync(string machineId, string viewerSessionId);
    Task LeaveViewerSessionAsync(string viewerSessionId);
    Task SendViewerPointerInputAsync(string machineId, string viewerSessionId, RemoteViewerPointerInputEvent input);
    Task SendViewerKeyboardInputAsync(string machineId, string viewerSessionId, RemoteViewerKeyboardInputEvent input);
}
