namespace AgentDeck.Shared.Enums;

/// <summary>Lifecycle state of a runner-hosted VS Code debug session.</summary>
public enum VsCodeDebugSessionStatus
{
    Requested,
    PreparingWorkspace,
    EnsuringExtensions,
    LaunchingHost,
    StartingDebugger,
    Running,
    Failed,
    Closed
}
