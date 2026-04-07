namespace AgentDeck.Shared.Enums;

/// <summary>Lifecycle state for a remote viewer session.</summary>
public enum RemoteViewerSessionStatus
{
    Requested,
    Preparing,
    Ready,
    Failed,
    Closed
}
