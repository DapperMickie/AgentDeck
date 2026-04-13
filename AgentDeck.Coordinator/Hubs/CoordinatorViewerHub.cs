using AgentDeck.Coordinator.Services;
using AgentDeck.Shared;
using AgentDeck.Shared.Hubs;
using AgentDeck.Shared.Models;
using Microsoft.AspNetCore.SignalR;

namespace AgentDeck.Coordinator.Hubs;

public sealed class CoordinatorViewerHub : Hub<IViewerHubClient>, ICoordinatorViewerHub
{
    private readonly ICompanionRegistryService _companions;
    private readonly IMachineRemoteControlRegistryService _remoteControl;
    private readonly RunnerBrokerService _runners;

    public CoordinatorViewerHub(
        ICompanionRegistryService companions,
        IMachineRemoteControlRegistryService remoteControl,
        RunnerBrokerService runners)
    {
        _companions = companions;
        _remoteControl = remoteControl;
        _runners = runners;
    }

    public override Task OnConnectedAsync()
    {
        var companionId = GetRequestedCompanionId();
        if (string.IsNullOrWhiteSpace(companionId))
        {
            throw new HubException("Coordinator companion identity is required before connecting to the viewer hub.");
        }

        _companions.AttachConnection(companionId, Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _companions.DisconnectConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public async Task JoinViewerSessionAsync(string machineId, string viewerSessionId)
    {
        RequireCompanionId();
        await Groups.AddToGroupAsync(Context.ConnectionId, GetViewerGroupName(viewerSessionId));

        var session = _runners.GetCachedViewerSession(viewerSessionId)
            ?? (await _runners.GetViewerSessionsAsync(machineId, Context.ConnectionAborted))
                .FirstOrDefault(candidate => string.Equals(candidate.Id, viewerSessionId, StringComparison.OrdinalIgnoreCase));
        if (session is not null)
        {
            await Clients.Caller.ViewerSessionUpdatedAsync(session);
        }

        var frame = _runners.GetLatestViewerFrame(viewerSessionId);
        if (frame is not null)
        {
            await Clients.Caller.ViewerFramePublishedAsync(frame);
        }
    }

    public Task LeaveViewerSessionAsync(string viewerSessionId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GetViewerGroupName(viewerSessionId));

    public Task SendViewerPointerInputAsync(string machineId, string viewerSessionId, RemoteViewerPointerInputEvent input)
    {
        var companionId = RequireCompanionId();
        EnsureControlAllowed(machineId, viewerSessionId, companionId);
        return _runners.SendViewerPointerInputAsync(machineId, viewerSessionId, companionId, input, Context.ConnectionAborted);
    }

    public Task SendViewerKeyboardInputAsync(string machineId, string viewerSessionId, RemoteViewerKeyboardInputEvent input)
    {
        var companionId = RequireCompanionId();
        EnsureControlAllowed(machineId, viewerSessionId, companionId);
        return _runners.SendViewerKeyboardInputAsync(machineId, viewerSessionId, companionId, input, Context.ConnectionAborted);
    }

    public static string GetViewerGroupName(string viewerSessionId) => $"viewer:{viewerSessionId}";

    private string RequireCompanionId() =>
        _companions.GetCompanionIdByConnection(Context.ConnectionId)
        ?? throw new HubException($"Coordinator does not recognize companion connection '{Context.ConnectionId}'.");

    private string? GetRequestedCompanionId()
    {
        var httpContext = Context.GetHttpContext();
        if (httpContext is null)
        {
            return null;
        }

        return httpContext.Request.Query[AgentDeckQueryNames.Companion].FirstOrDefault()
            ?? httpContext.Request.Headers[AgentDeckHeaderNames.Companion].FirstOrDefault();
    }

    private void EnsureControlAllowed(string machineId, string viewerSessionId, string companionId)
    {
        var state = _remoteControl.GetState(machineId);
        if (state is null ||
            !string.Equals(state.ViewerSessionId, viewerSessionId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state.ControllerCompanionId, companionId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new HubException(
            $"Machine '{state.MachineName ?? state.MachineId}' is currently remotely controlled by companion '{state.ControllerDisplayName ?? state.ControllerCompanionId}'.");
    }
}
