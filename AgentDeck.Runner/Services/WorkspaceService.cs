using AgentDeck.Runner.Configuration;
using AgentDeck.Shared.Models;
using Microsoft.Extensions.Options;

namespace AgentDeck.Runner.Services;

/// <inheritdoc />
public sealed class WorkspaceService : IWorkspaceService
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _root;

    public WorkspaceService(IOptions<RunnerOptions> options)
    {
        _root = Path.GetFullPath(
            string.IsNullOrWhiteSpace(options.Value.WorkspaceRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AgentDeck")
                : options.Value.WorkspaceRoot);

        Directory.CreateDirectory(_root);
    }

    public string GetWorkspaceRoot() => _root;

    public string ResolvePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(_root, path));

        EnsureUnderRoot(full);
        return full;
    }

    public string ResolveDirectory(string relativePath)
    {
        return ResolvePath(relativePath);
    }

    public void CreateDirectory(string relativePath)
    {
        var full = ResolveDirectory(relativePath);
        Directory.CreateDirectory(full);
    }

    public WorkspaceInfo GetWorkspaceInfo()
    {
        var dirs = Directory.Exists(_root)
            ? Directory.GetDirectories(_root).Select(Path.GetFileName).OfType<string>().ToList()
            : new List<string>();

        return new WorkspaceInfo { RootPath = _root, Directories = dirs };
    }

    private void EnsureUnderRoot(string fullPath)
    {
        // Append separator to prevent "C:\workspace" matching "C:\workspaceEvil"
        var rootWithSep = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;

        if (!fullPath.Equals(_root, PathComparison) &&
            !fullPath.StartsWith(rootWithSep, PathComparison))
        {
            throw new InvalidOperationException(
                $"Path '{fullPath}' is outside the workspace root '{_root}'.");
        }
    }
}
