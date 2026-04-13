using AgentDeck.Runner.Services;
using Microsoft.AspNetCore.SignalR;
using RdpPoc.Contracts;

namespace AgentDeck.Runner.Hubs;

public sealed class ManagedViewerRelayHub : Hub
{
    private readonly IManagedViewerRelayService _relayService;

    public ManagedViewerRelayHub(IManagedViewerRelayService relayService)
    {
        _relayService = relayService;
    }

    public async Task JoinSessionAsViewer(string sessionId, string viewerAccessToken)
    {
        var session = _relayService.JoinViewer(sessionId, viewerAccessToken);
        await Groups.AddToGroupAsync(Context.ConnectionId, GetViewerGroupName(sessionId));
        await Clients.Caller.SendAsync("SessionUpdated", session);

        var latestFrame = _relayService.GetLatestFrame(sessionId);
        if (latestFrame is not null)
        {
            await Clients.Caller.SendAsync("FramePublished", latestFrame);
        }
    }

    public Task SendPointerInput(string sessionId, string viewerAccessToken, PointerInputEvent pointerInput) =>
        _relayService.SendPointerInputAsync(sessionId, viewerAccessToken, pointerInput);

    public Task SendKeyboardInput(string sessionId, string viewerAccessToken, KeyboardInputEvent keyboardInput) =>
        _relayService.SendKeyboardInputAsync(sessionId, viewerAccessToken, keyboardInput);

    public static string GetViewerGroupName(string sessionId) => $"viewer:{sessionId}";
}
