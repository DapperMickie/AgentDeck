using AgentDeck.Shared.Models;

namespace AgentDeck.Coordinator.Services;

public interface IProjectRegistryService
{
    IReadOnlyList<ProjectDefinition> GetProjects();
    ProjectDefinition? GetProject(string projectId);
    ProjectDefinition UpsertProject(string projectId, ProjectDefinition project);
    ProjectDefinition UpsertWorkspace(string projectId, ProjectWorkspaceMapping workspace);
}
