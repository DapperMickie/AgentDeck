using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Top-level shared contract for a project that AgentDeck can orchestrate.</summary>
public sealed class ProjectDefinition
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public ProjectWorkloadKind Workload { get; init; }
    public ProjectRepositoryReference Repository { get; init; } = new();
    public IReadOnlyList<ProjectWorkspaceMapping> Workspaces { get; init; } = [];
    public IReadOnlyList<ProjectTargetDefinition> Targets { get; init; } = [];
    public IReadOnlyList<ProjectLaunchProfile> LaunchProfiles { get; init; } = [];
}
