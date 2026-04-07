using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Request payload for progressing or closing a remote viewer session.</summary>
public sealed class UpdateRemoteViewerSessionRequest
{
    public RemoteViewerSessionStatus Status { get; init; }
    public string? ConnectionUri { get; init; }
    public string? AccessToken { get; init; }
    public string? Message { get; init; }
}
