using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

/// <summary>Manages the runner's workspace root directory.</summary>
public interface IWorkspaceService
{
    string GetWorkspaceRoot();
    string ResolvePath(string path);
    string ResolveDirectory(string relativePath);
    void CreateDirectory(string relativePath);
    WorkspaceInfo GetWorkspaceInfo();
}
