using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Represents a viewing provider that a runner can potentially use.</summary>
public sealed class RemoteViewerProviderCapability
{
    public RemoteViewerProviderKind Provider { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public IReadOnlyList<RemoteViewerTargetKind> SupportedTargets { get; init; } = [];
    public bool RequiresInteractiveDesktop { get; init; }
    public string? Notes { get; init; }
}
