using AgentDeck.Coordinator.Configuration;
using AgentDeck.Coordinator.Services;
using AgentDeck.Shared;
using AgentDeck.Shared.Hubs;
using AgentDeck.Shared.Models;
using AgentDeck.Shared.Protocol;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.SignalR;

namespace AgentDeck.Coordinator.Hubs;

public sealed class CoordinatorRunnerHub : Hub<IRunnerControlClient>, ICoordinatorRunnerHub
{
    private readonly RunnerBrokerService _runners;
    private readonly CoordinatorOptions _options;

    public CoordinatorRunnerHub(RunnerBrokerService runners, IOptions<CoordinatorOptions> options)
    {
        _runners = runners;
        _options = options.Value;
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

    public Task<HubProtocolHelloAck> HelloAsync(HubProtocolHello hello) =>
        Task.FromResult(NegotiateProtocol(hello, "coordinator-runner"));

    public Task PublishTerminalOutputAsync(TerminalOutput output) =>
        _runners.PublishTerminalOutputAsync(RequireMachineId(), output, Context.ConnectionAborted);

    public Task PublishTerminalSessionUpdatedAsync(TerminalSession session) =>
        _runners.PublishTerminalSessionUpdatedAsync(RequireMachineId(), session, Context.ConnectionAborted);

    public Task PublishTerminalSessionClosedAsync(string sessionId) =>
        _runners.PublishTerminalSessionClosedAsync(RequireMachineId(), sessionId, Context.ConnectionAborted);

    public Task PublishOrchestrationJobUpdatedAsync(OrchestrationJob job) =>
        _runners.PublishOrchestrationJobUpdatedAsync(RequireMachineId(), job, Context.ConnectionAborted);

    public Task PublishViewerSessionUpdatedAsync(RemoteViewerSession session) =>
        _runners.PublishViewerSessionUpdatedAsync(RequireMachineId(), session, Context.ConnectionAborted);

    public Task PublishViewerFrameAsync(RemoteViewerRelayFrame frame) =>
        _runners.PublishViewerFrameAsync(RequireMachineId(), frame, Context.ConnectionAborted);


    private HubProtocolHelloAck NegotiateProtocol(HubProtocolHello hello, string serverKind)
    {
        if (hello.ProtocolVersion < _options.MinimumSupportedProtocolVersion ||
            hello.ProtocolVersion > _options.MaximumSupportedProtocolVersion)
        {
            throw new HubException($"Incompatible AgentDeck hub protocol {hello.ProtocolVersion}. Supported range is {_options.MinimumSupportedProtocolVersion}-{_options.MaximumSupportedProtocolVersion}.");
        }

        return new HubProtocolHelloAck
        {
            ProtocolVersion = Math.Min(hello.ProtocolVersion, _options.MaximumSupportedProtocolVersion),
            MinimumSupportedProtocolVersion = _options.MinimumSupportedProtocolVersion,
            MaximumSupportedProtocolVersion = _options.MaximumSupportedProtocolVersion,
            ServerKind = serverKind
        };
    }

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
