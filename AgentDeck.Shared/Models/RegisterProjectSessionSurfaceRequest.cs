using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

public sealed class RegisterProjectSessionSurfaceRequest
{
    public string? SurfaceId { get; init; }
    public ProjectSessionSurfaceKind Kind { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? MachineId { get; init; }
    public string? MachineName { get; init; }
    public string? ReferenceId { get; init; }
    public string? JobId { get; init; }
    public ApplicationTargetPlatform? Platform { get; init; }
    public ProjectLaunchMode? LaunchMode { get; init; }
    public RemoteViewerTargetKind? ViewerTargetKind { get; init; }
    public ProjectSessionSurfaceStatus Status { get; init; } = ProjectSessionSurfaceStatus.Ready;
    public string? StatusMessage { get; init; }
}
