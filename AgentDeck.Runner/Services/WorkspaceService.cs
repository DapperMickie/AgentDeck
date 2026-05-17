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
    private readonly string _realizedRoot;

    public WorkspaceService(IOptions<RunnerOptions> options)
    {
        _root = Path.GetFullPath(
            string.IsNullOrWhiteSpace(options.Value.WorkspaceRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AgentDeck")
                : options.Value.WorkspaceRoot);

        Directory.CreateDirectory(_root);
        _realizedRoot = ResolveSymlinksToDeepestExisting(_root);
    }

    public string GetWorkspaceRoot() => _root;

    public string ResolvePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(_root, path));

        EnsureUnderRoot(full);

        // If the path (or any of its existing ancestors) is a symlink, walk the
        // chain and re-check that the realized target is still under the
        // realized root. We only realize the deepest existing portion so callers
        // can ResolvePath("new/file.txt") before creating it.
        var realized = ResolveSymlinksToDeepestExisting(full);
        EnsureUnderRealizedRoot(realized);

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

    private void EnsureUnderRealizedRoot(string realizedPath)
    {
        var rootWithSep = _realizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;

        if (!realizedPath.Equals(_realizedRoot, PathComparison) &&
            !realizedPath.StartsWith(rootWithSep, PathComparison))
        {
            throw new InvalidOperationException(
                $"Resolved path '{realizedPath}' (after symlink resolution) is outside the workspace root '{_realizedRoot}'.");
        }
    }

    // Walks up from fullPath to the deepest ancestor that exists on disk and
    // resolves the entire chain of symlinks at each level. Returns the fully
    // realized path. Non-existent leaf segments are preserved verbatim so
    // callers can ResolvePath("new/file.txt") before creating it.
    private static string ResolveSymlinksToDeepestExisting(string fullPath)
    {
        var segments = new List<string>();
        var current = fullPath;

        while (!string.IsNullOrEmpty(current) && !File.Exists(current) && !Directory.Exists(current))
        {
            var name = Path.GetFileName(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(parent) || parent == current)
            {
                break;
            }
            segments.Add(name);
            current = parent;
        }

        if (string.IsNullOrEmpty(current))
        {
            return fullPath;
        }

        var realizedAnchor = RealizePath(current);

        if (segments.Count == 0)
        {
            return realizedAnchor;
        }

        segments.Reverse();
        return Path.GetFullPath(Path.Combine(new[] { realizedAnchor }.Concat(segments).ToArray()));
    }

    private static string RealizePath(string path)
    {
        // Cap symlink resolution depth to avoid loops.
        const int maxDepth = 40;
        var current = path;
        for (var i = 0; i < maxDepth; i++)
        {
            FileSystemInfo? info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current) ? new FileInfo(current) : null;

            if (info is null)
            {
                return Path.GetFullPath(current);
            }

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null)
            {
                return Path.GetFullPath(info.FullName);
            }

            current = target.FullName;
        }

        throw new InvalidOperationException($"Symlink chain too deep while resolving '{path}'.");
    }
}
