using AgentDeck.Runner.Configuration;
using AgentDeck.Shared.Models;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text.RegularExpressions;

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
        var directories = Directory.Exists(_root)
            ? Directory.GetDirectories(_root)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        var entries = directories
            .Select(directory => CreateDirectoryInfo(directory, Path.GetFileName(directory)))
            .ToList();

        return new WorkspaceInfo
        {
            RootPath = _root,
            Directories = entries.Select(entry => entry.Name).ToList(),
            Entries = entries
        };
    }

    public WorkspaceDirectoryInfo InspectDirectory(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var fullPath = ResolveDirectory(relativePath);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Workspace directory '{relativePath}' was not found.");
        }

        var normalizedRelativePath = Path.GetRelativePath(_root, fullPath);
        return CreateDirectoryInfo(fullPath, normalizedRelativePath);
    }

    private static WorkspaceDirectoryInfo CreateDirectoryInfo(string directory, string relativePath)
    {
        var info = new DirectoryInfo(directory);
        var name = Path.GetFileName(directory);
        return new WorkspaceDirectoryInfo
        {
            Name = name,
            RelativePath = relativePath,
            FullPath = directory,
            LastWriteTimeUtc = info.Exists ? info.LastWriteTimeUtc : null,
            Repository = GetRepositoryState(directory)
        };
    }

    private static WorkspaceRepositoryState? GetRepositoryState(string directory)
    {
        var insideWorkTree = RunGit(directory, "rev-parse --is-inside-work-tree");
        if (insideWorkTree.ExitCode != 0)
        {
            return null;
        }

        if (!string.Equals(insideWorkTree.StandardOutput.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var root = RunGit(directory, "rev-parse --show-toplevel");
        var branch = RunGit(directory, "rev-parse --abbrev-ref HEAD");
        var head = RunGit(directory, "rev-parse --short=12 HEAD");
        var remote = RunGit(directory, "remote get-url origin");
        var status = RunGit(directory, "status --porcelain=v1 -b");

        var scanErrors = new[] { root, branch, head, status }
            .Where(result => result.ExitCode != 0 && !string.IsNullOrWhiteSpace(result.ErrorOrOutput))
            .Select(result => result.ErrorOrOutput)
            .ToList();

        var (modifiedCount, untrackedCount, aheadBy, behindBy) = ParseStatus(status.StandardOutput);
        var branchName = branch.ExitCode == 0 ? NullIfBlank(branch.StandardOutput.Trim()) : null;

        return new WorkspaceRepositoryState
        {
            IsRepository = true,
            RootPath = root.ExitCode == 0 ? NullIfBlank(root.StandardOutput.Trim()) : null,
            Branch = string.Equals(branchName, "HEAD", StringComparison.OrdinalIgnoreCase) ? null : branchName,
            HeadSha = head.ExitCode == 0 ? NullIfBlank(head.StandardOutput.Trim()) : null,
            RemoteUrl = remote.ExitCode == 0 ? NullIfBlank(remote.StandardOutput.Trim()) : null,
            ModifiedCount = modifiedCount,
            UntrackedCount = untrackedCount,
            HasUncommittedChanges = modifiedCount > 0 || untrackedCount > 0,
            AheadBy = aheadBy,
            BehindBy = behindBy,
            ScanError = scanErrors.Count == 0 ? null : string.Join(" ", scanErrors)
        };
    }

    private static (int ModifiedCount, int UntrackedCount, int AheadBy, int BehindBy) ParseStatus(string statusOutput)
    {
        var modifiedCount = 0;
        var untrackedCount = 0;
        var aheadBy = 0;
        var behindBy = 0;

        foreach (var rawLine in statusOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawLine.StartsWith("## ", StringComparison.Ordinal))
            {
                aheadBy = ParseStatusCounter(rawLine, "ahead");
                behindBy = ParseStatusCounter(rawLine, "behind");
                continue;
            }

            if (rawLine.StartsWith("??", StringComparison.Ordinal))
            {
                untrackedCount++;
            }
            else
            {
                modifiedCount++;
            }
        }

        return (modifiedCount, untrackedCount, aheadBy, behindBy);
    }

    private static int ParseStatusCounter(string line, string label)
    {
        var match = Regex.Match(line, $@"\b{Regex.Escape(label)} (?<count>\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["count"].Value, out var count) ? count : 0;
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static GitCommandResult RunGit(string workingDirectory, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            if (!process.WaitForExit(milliseconds: 2_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort: diagnostics should never break workspace listing.
                }

                return new GitCommandResult(-1, string.Empty, "git command timed out");
            }

            return new GitCommandResult(
                process.ExitCode,
                process.StandardOutput.ReadToEnd(),
                process.StandardError.ReadToEnd());
        }
        catch (Exception ex)
        {
            return new GitCommandResult(-1, string.Empty, ex.Message);
        }
    }

    private sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string ErrorOrOutput => string.IsNullOrWhiteSpace(StandardError) ? StandardOutput.Trim() : StandardError.Trim();
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
