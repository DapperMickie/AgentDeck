using AgentDeck.Shared.Hubs;
using AgentDeck.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace AgentDeck.Runner.Services;

public sealed class CoordinatorRunnerPublisher : ICoordinatorRunnerPublisher
{
    private readonly CoordinatorRunnerConnectionState _state;

    public CoordinatorRunnerPublisher(CoordinatorRunnerConnectionState state)
    {
        _state = state;
    }

    public Task PublishTerminalOutputAsync(TerminalOutput output, CancellationToken cancellationToken = default) =>
        SendAsync(nameof(ICoordinatorRunnerHub.PublishTerminalOutputAsync), [output], cancellationToken);

    public Task PublishTerminalSessionUpdatedAsync(TerminalSession session, CancellationToken cancellationToken = default) =>
        SendAsync(nameof(ICoordinatorRunnerHub.PublishTerminalSessionUpdatedAsync), [session], cancellationToken);

    public Task PublishTerminalSessionClosedAsync(string sessionId, CancellationToken cancellationToken = default) =>
        SendAsync(nameof(ICoordinatorRunnerHub.PublishTerminalSessionClosedAsync), [sessionId], cancellationToken);

    public Task PublishViewerSessionUpdatedAsync(RemoteViewerSession session, CancellationToken cancellationToken = default) =>
        SendAsync(nameof(ICoordinatorRunnerHub.PublishViewerSessionUpdatedAsync), [session], cancellationToken);

    public Task PublishViewerFrameAsync(RemoteViewerRelayFrame frame, CancellationToken cancellationToken = default) =>
        SendAsync(nameof(ICoordinatorRunnerHub.PublishViewerFrameAsync), [frame], cancellationToken);

    private async Task SendAsync(string methodName, object?[] args, CancellationToken cancellationToken)
    {
        var connection = _state.Connection;
        if (connection is null || !connection.State.Equals(HubConnectionState.Connected))
        {
            return;
        }

        await connection.SendCoreAsync(methodName, args, cancellationToken);
    }
}
