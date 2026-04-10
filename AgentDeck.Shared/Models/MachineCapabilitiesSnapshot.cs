namespace AgentDeck.Shared.Models;

/// <summary>Snapshot of supported tool detection results for a runner machine.</summary>
public sealed class MachineCapabilitiesSnapshot
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public MachinePlatformProfile Platform { get; init; } = new();
    public IReadOnlyList<MachineTargetSupport> SupportedTargets { get; init; } = [];
    public IReadOnlyList<RemoteViewerProviderCapability> RemoteViewerProviders { get; init; } = [];
    public IReadOnlyList<MachineCapability> Capabilities { get; init; } = [];
}
