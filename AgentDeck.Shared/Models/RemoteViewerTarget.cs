using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Describes what a remote viewer session should display.</summary>
public sealed class RemoteViewerTarget
{
    public RemoteViewerTargetKind Kind { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? JobId { get; init; }
    public string? SessionId { get; init; }
    public string? WindowTitle { get; init; }
    public string? DeviceProfile { get; init; }
}
