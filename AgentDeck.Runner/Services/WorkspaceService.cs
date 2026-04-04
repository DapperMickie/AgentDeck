using AgentDeck.Runner.Configuration;
using AgentDeck.Shared.Models;
using Microsoft.Extensions.Options;

namespace AgentDeck.Runner.Services;

/// <inheritdoc />
public sealed class WorkspaceService : IWorkspaceService
{
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

    public string ResolveDirectory(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relativePath));
        EnsureUnderRoot(full);
        return full;
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
        if (!fullPath.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path '{fullPath}' is outside the workspace root '{_root}'.");
    }
}
