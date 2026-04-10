using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Coordinator-owned machine-level remote control ownership snapshot.</summary>
public sealed class MachineRemoteControlState
{
    public required string MachineId { get; init; } = string.Empty;
    public string? MachineName { get; init; }
    public required string ControllerCompanionId { get; init; } = string.Empty;
    public string? ControllerDisplayName { get; init; }
    public required string ViewerSessionId { get; init; } = string.Empty;
    public RemoteViewerTargetKind TargetKind { get; init; } = RemoteViewerTargetKind.Desktop;
    public string? TargetDisplayName { get; init; }
    public RemoteViewerProviderKind Provider { get; init; } = RemoteViewerProviderKind.Auto;
    public RemoteViewerSessionStatus ViewerStatus { get; init; } = RemoteViewerSessionStatus.Requested;
    public string? ConnectionUri { get; init; }
    public string? StatusMessage { get; init; }
    public DateTimeOffset AcquiredAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
