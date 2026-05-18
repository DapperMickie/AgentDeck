using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

public sealed class RunnerOrchestratorProviderCapability
{
    public bool SupportsLinux { get; init; }
    public bool SupportsWindows { get; init; }
    public bool SupportsMacOS { get; init; }
    public bool SupportsContainers { get; init; }
    public bool SupportsVirtualMachines { get; init; }
    public bool SupportsGpu { get; init; }
    public bool SupportsVolumes { get; init; }
    public bool SupportsSnapshots { get; init; }
    public IReadOnlyList<RemoteViewerProviderKind> ViewerProviders { get; init; } = [];
}
