namespace AgentDeck.Shared.Models;

/// <summary>Information about the runner's workspace root and its contents.</summary>
public sealed class WorkspaceInfo
{
    /// <summary>Absolute path to the workspace root directory.</summary>
    public required string RootPath { get; init; }

    /// <summary>Names of top-level directories inside the workspace root.</summary>
    public required IReadOnlyList<string> Directories { get; init; }

    /// <summary>
    /// Rich metadata for top-level workspace directories. The legacy <see cref="Directories" /> list is
    /// kept for older companions; new clients should prefer this collection when choosing a runner/workspace.
    /// </summary>
    public IReadOnlyList<WorkspaceDirectoryInfo> Entries { get; init; } = [];
}

/// <summary>Request to inspect a workspace directory relative to the runner workspace root.</summary>
public sealed class InspectWorkspaceRequest
{
    public string RelativePath { get; init; } = string.Empty;
}

/// <summary>Metadata for a directory inside a runner workspace.</summary>
public sealed class WorkspaceDirectoryInfo
{
    public required string Name { get; init; }
    public required string RelativePath { get; init; }
    public required string FullPath { get; init; }
    public DateTimeOffset? LastWriteTimeUtc { get; init; }
    public WorkspaceRepositoryState? Repository { get; init; }
}

/// <summary>Best-effort local Git repository state for a workspace directory.</summary>
public sealed class WorkspaceRepositoryState
{
    public bool IsRepository { get; init; }
    public string? RootPath { get; init; }
    public string? Branch { get; init; }
    public string? HeadSha { get; init; }
    public string? RemoteUrl { get; init; }
    public bool HasUncommittedChanges { get; init; }
    public int ModifiedCount { get; init; }
    public int UntrackedCount { get; init; }
    public int AheadBy { get; init; }
    public int BehindBy { get; init; }
    public string? ScanError { get; init; }
}
