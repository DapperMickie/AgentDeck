using AgentDeck.Shared.Models;
using RdpPoc.Contracts;

namespace AgentDeck.Runner.Services;

public interface IManagedViewerRelayService
{
    Task<ManagedViewerRelayBootstrapResult> StartAsync(
        RemoteViewerSession session,
        string connectionBaseUri,
        CancellationToken cancellationToken = default);

    Task StopAsync(string sessionId, CancellationToken cancellationToken = default);

    Task PublishSessionUpdatedAsync(RemoteViewerSession session, CancellationToken cancellationToken = default);

    RemoteViewerSession JoinViewer(string sessionId, string accessToken);

    RelayFrame? GetLatestFrame(string sessionId);

    Task SendPointerInputAsync(
        string sessionId,
        string accessToken,
        PointerInputEvent input,
        CancellationToken cancellationToken = default);

    Task SendKeyboardInputAsync(
        string sessionId,
        string accessToken,
        KeyboardInputEvent input,
        CancellationToken cancellationToken = default);
}

public sealed record ManagedViewerRelayBootstrapResult(
    string ConnectionUri,
    string AccessToken,
    string Message);
