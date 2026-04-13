using AgentDeck.Coordinator.Services;
using AgentDeck.Shared;
using AgentDeck.Shared.Hubs;
using AgentDeck.Shared.Models;
using Microsoft.AspNetCore.SignalR;

namespace AgentDeck.Coordinator.Hubs;

public sealed class CoordinatorRunnerHub : Hub<IRunnerControlClient>, ICoordinatorRunnerHub
{
    private readonly RunnerBrokerService _runners;

    public CoordinatorRunnerHub(RunnerBrokerService runners)
    {
        _runners = runners;
    }

    public override Task OnConnectedAsync()
    {
        var machineId = GetRequestedMachineId();
        if (string.IsNullOrWhiteSpace(machineId))
        {
            throw new HubException("Runner machine identity is required before connecting to the coordinator runner hub.");
        }

        _runners.AttachRunnerConnection(machineId, Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _runners.DetachRunnerConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public Task PublishTerminalOutputAsync(TerminalOutput output) =>
        _runners.PublishTerminalOutputAsync(RequireMachineId(), output, Context.ConnectionAborted);

    public Task PublishTerminalSessionUpdatedAsync(TerminalSession session) =>
        _runners.PublishTerminalSessionUpdatedAsync(RequireMachineId(), session, Context.ConnectionAborted);

    public Task PublishTerminalSessionClosedAsync(string sessionId) =>
        _runners.PublishTerminalSessionClosedAsync(RequireMachineId(), sessionId, Context.ConnectionAborted);

    public Task PublishViewerSessionUpdatedAsync(RemoteViewerSession session) =>
        _runners.PublishViewerSessionUpdatedAsync(RequireMachineId(), session, Context.ConnectionAborted);

    public Task PublishViewerFrameAsync(RemoteViewerRelayFrame frame) =>
        _runners.PublishViewerFrameAsync(RequireMachineId(), frame, Context.ConnectionAborted);

    private string RequireMachineId() =>
        GetRequestedMachineId()
        ?? throw new HubException($"Coordinator does not recognize runner connection '{Context.ConnectionId}'.");

    private string? GetRequestedMachineId()
    {
        var httpContext = Context.GetHttpContext();
        if (httpContext is null)
        {
            return null;
        }

        return httpContext.Request.Query[AgentDeckQueryNames.Machine].FirstOrDefault()
            ?? httpContext.Request.Headers[AgentDeckHeaderNames.Machine].FirstOrDefault();
    }
}
