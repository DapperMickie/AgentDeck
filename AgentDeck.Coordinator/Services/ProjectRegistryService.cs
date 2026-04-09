using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

namespace AgentDeck.Coordinator.Services;

public sealed class ProjectRegistryService : IProjectRegistryService
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ProjectDefinition> _projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ProjectRegistryService> _logger;

    public ProjectRegistryService(ILogger<ProjectRegistryService> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<ProjectDefinition> GetProjects()
    {
        lock (_lock)
        {
            return _projects.Values
                .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public ProjectDefinition? GetProject(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        lock (_lock)
        {
            return _projects.TryGetValue(projectId, out var project)
                ? project
                : null;
        }
    }

    public ProjectDefinition UpsertProject(string projectId, ProjectDefinition project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(project);

        var normalizedProject = NormalizeProject(projectId, project);
        lock (_lock)
        {
            _projects[normalizedProject.Id] = normalizedProject;
        }

        _logger.LogInformation(
            "Upserted project {ProjectId} ({ProjectName}) workload {Workload} with {WorkspaceCount} workspaces",
            normalizedProject.Id,
            normalizedProject.Name,
            normalizedProject.Workload,
            normalizedProject.Workspaces.Count);

        return normalizedProject;
    }

    public ProjectDefinition UpsertWorkspace(string projectId, ProjectWorkspaceMapping workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace.MachineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace.ProjectPath);

        lock (_lock)
        {
            if (!_projects.TryGetValue(projectId, out var existingProject))
            {
                throw new InvalidOperationException($"Coordinator does not know project '{projectId}'.");
            }

            var normalizedWorkspace = NormalizeWorkspace(workspace);
            var otherWorkspaces = existingProject.Workspaces
                .Where(existing => !string.Equals(existing.MachineId, normalizedWorkspace.MachineId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var shouldBePrimary = normalizedWorkspace.IsPrimary || !otherWorkspaces.Any(existing => existing.IsPrimary);
            normalizedWorkspace = new ProjectWorkspaceMapping
            {
                MachineId = normalizedWorkspace.MachineId,
                MachineName = normalizedWorkspace.MachineName,
                ProjectPath = normalizedWorkspace.ProjectPath,
                IsPrimary = shouldBePrimary
            };

            var workspaces = otherWorkspaces
                .Select(existing => shouldBePrimary && existing.IsPrimary
                    ? new ProjectWorkspaceMapping
                    {
                        MachineId = existing.MachineId,
                        MachineName = existing.MachineName,
                        ProjectPath = existing.ProjectPath,
                        IsPrimary = false
                    }
                    : existing)
                .Append(normalizedWorkspace)
                .ToArray();

            var updatedProject = new ProjectDefinition
            {
                Id = existingProject.Id,
                Name = existingProject.Name,
                Workload = existingProject.Workload,
                Repository = existingProject.Repository,
                Targets = existingProject.Targets,
                LaunchProfiles = existingProject.LaunchProfiles,
                Workspaces = workspaces
            };

            _projects[existingProject.Id] = updatedProject;
            _logger.LogInformation(
                "Updated workspace for project {ProjectId} on machine {MachineId} at {ProjectPath}",
                existingProject.Id,
                normalizedWorkspace.MachineId,
                normalizedWorkspace.ProjectPath);
            return updatedProject;
        }
    }

    private static ProjectDefinition NormalizeProject(string routeProjectId, ProjectDefinition project)
    {
        var normalizedRouteProjectId = NormalizeRequired(routeProjectId, nameof(routeProjectId));
        var repository = project.Repository ?? new ProjectRepositoryReference();
        var workspaces = project.Workspaces ?? [];
        var targets = project.Targets ?? [];
        var launchProfiles = project.LaunchProfiles ?? [];
        var projectName = Normalize(project.Name)
            ?? Normalize(repository.Name)
            ?? normalizedRouteProjectId;
        var template = ProjectTemplateCatalog.CreateDefault(project.Workload, projectName);

        var normalizedWorkspaces = workspaces
            .Select(NormalizeWorkspace)
            .GroupBy(workspace => workspace.MachineId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToArray();

        if (normalizedWorkspaces.Any(workspace => string.IsNullOrWhiteSpace(workspace.MachineId) || string.IsNullOrWhiteSpace(workspace.ProjectPath)))
        {
            throw new ArgumentException("Project workspaces must include non-empty machine IDs and project paths.", nameof(project));
        }

        return new ProjectDefinition
        {
            Id = normalizedRouteProjectId,
            Name = projectName,
            Workload = project.Workload,
            Repository = NormalizeRepository(repository, projectName),
            Workspaces = normalizedWorkspaces,
            Targets = targets.Count > 0 ? NormalizeTargets(targets, project.Workload) : template.Targets,
            LaunchProfiles = launchProfiles.Count > 0 ? NormalizeLaunchProfiles(launchProfiles) : template.LaunchProfiles
        };
    }

    private static ProjectRepositoryReference NormalizeRepository(ProjectRepositoryReference repository, string projectName)
    {
        return new ProjectRepositoryReference
        {
            Name = Normalize(repository.Name) ?? projectName,
            Owner = Normalize(repository.Owner),
            Url = Normalize(repository.Url),
            DefaultBranch = Normalize(repository.DefaultBranch) ?? "main"
        };
    }

    private static IReadOnlyList<ProjectTargetDefinition> NormalizeTargets(
        IReadOnlyList<ProjectTargetDefinition> targets,
        ProjectWorkloadKind workload)
    {
        return targets
            .Where(target => !string.IsNullOrWhiteSpace(target.DisplayName))
            .GroupBy(target => target.Platform)
            .Select(group =>
            {
                var target = group.Last();
                return new ProjectTargetDefinition
                {
                    Workload = workload,
                    Platform = target.Platform,
                    DisplayName = target.DisplayName.Trim(),
                    CapabilityId = Normalize(target.CapabilityId),
                    RequiresVsCodeForDebugging = target.RequiresVsCodeForDebugging,
                    PackageRequirement = Normalize(target.PackageRequirement),
                    Notes = Normalize(target.Notes)
                };
            })
            .ToArray();
    }

    private static IReadOnlyList<ProjectLaunchProfile> NormalizeLaunchProfiles(IReadOnlyList<ProjectLaunchProfile> profiles)
    {
        return profiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Id) && !string.IsNullOrWhiteSpace(profile.DisplayName))
            .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var profile = group.Last();
                return new ProjectLaunchProfile
                {
                    Id = profile.Id.Trim(),
                    DisplayName = profile.DisplayName.Trim(),
                    Platform = profile.Platform,
                    Mode = profile.Mode,
                    LaunchDriver = profile.LaunchDriver,
                    PreferredMachineRole = profile.PreferredMachineRole,
                    PreferredMachineId = Normalize(profile.PreferredMachineId),
                    BuildCommand = Normalize(profile.BuildCommand) ?? "dotnet build",
                    LaunchCommand = Normalize(profile.LaunchCommand),
                    BootstrapCommand = Normalize(profile.BootstrapCommand),
                    DebugConfigurationName = Normalize(profile.DebugConfigurationName),
                    RequiresVsCode = profile.RequiresVsCode,
                    RequiresEmulator = profile.RequiresEmulator,
                    RequiresSimulator = profile.RequiresSimulator,
                    DeviceCatalog = profile.DeviceCatalog,
                    DefaultDeviceProfileId = Normalize(profile.DefaultDeviceProfileId),
                    Notes = Normalize(profile.Notes)
                };
            })
            .ToArray();
    }

    private static ProjectWorkspaceMapping NormalizeWorkspace(ProjectWorkspaceMapping workspace)
    {
        return new ProjectWorkspaceMapping
        {
            MachineId = Normalize(workspace.MachineId) ?? string.Empty,
            MachineName = Normalize(workspace.MachineName),
            ProjectPath = Normalize(workspace.ProjectPath) ?? string.Empty,
            IsPrimary = workspace.IsPrimary
        };
    }

    private static string NormalizeRequired(string value, string parameterName) =>
        Normalize(value) ?? throw new ArgumentException("Value is required.", parameterName);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
