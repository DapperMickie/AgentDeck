using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Request payload for creating a remote viewer session.</summary>
public sealed class CreateRemoteViewerSessionRequest
{
    public string? MachineId { get; init; }
    public string? MachineName { get; init; }
    public string? JobId { get; init; }
    public RemoteViewerTarget Target { get; init; } = new();
    public RemoteViewerProviderKind Provider { get; init; } = RemoteViewerProviderKind.Auto;
}
