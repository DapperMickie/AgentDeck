using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

/// <summary>Tracks remote viewer sessions and provider capabilities for the local runner machine.</summary>
public interface IRemoteViewerSessionService
{
    IReadOnlyList<RemoteViewerProviderCapability> GetAvailableProviders();
    RemoteViewerSession Create(CreateRemoteViewerSessionRequest request);
    RemoteViewerSession? Get(string sessionId);
    IReadOnlyList<RemoteViewerSession> GetAll();
    RemoteViewerSessionMutationResult Update(string sessionId, UpdateRemoteViewerSessionRequest request);
    RemoteViewerSessionMutationResult Close(string sessionId, string? message = null);
}
