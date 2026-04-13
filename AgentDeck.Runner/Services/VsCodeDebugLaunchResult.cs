namespace AgentDeck.Runner.Services;

/// <summary>Describes a launched VS Code debug host session.</summary>
public sealed class VsCodeDebugLaunchResult
{
    public required string DebugSessionId { get; init; }
    public required string ViewerSessionId { get; init; }
    public required string WorkspaceDirectory { get; init; }
    public required string StartupProjectPath { get; init; }
    public required string LaunchCommand { get; init; }
    public required IReadOnlyList<string> LaunchArguments { get; init; }
}
