namespace AgentDeck.Shared.Models;

/// <summary>Information about the runner's workspace root and its contents.</summary>
public sealed class WorkspaceInfo
{
    /// <summary>Absolute path to the workspace root directory.</summary>
    public required string RootPath { get; init; }

    /// <summary>Names of top-level directories inside the workspace root.</summary>
    public required IReadOnlyList<string> Directories { get; init; }
}
