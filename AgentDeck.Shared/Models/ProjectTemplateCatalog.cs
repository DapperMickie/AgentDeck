using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Creates default project contracts and launch profiles for supported workloads.</summary>
public static class ProjectTemplateCatalog
{
    /// <summary>Creates a default project definition for a supported workload.</summary>
    public static ProjectDefinition CreateDefault(ProjectWorkloadKind workload, string projectName)
    {
        var normalizedProjectName = string.IsNullOrWhiteSpace(projectName) ? workload.ToString() : projectName.Trim();
        var targets = ProjectTargetCatalog.GetTargets(workload);

        return new ProjectDefinition
        {
            Id = normalizedProjectName.ToLowerInvariant().Replace(' ', '-'),
            Name = normalizedProjectName,
            Workload = workload,
            Repository = new ProjectRepositoryReference
            {
                Name = normalizedProjectName,
                DefaultBranch = "main"
            },
            Targets = targets,
            LaunchProfiles = CreateLaunchProfiles(workload, targets)
        };
    }

    private static IReadOnlyList<ProjectLaunchProfile> CreateLaunchProfiles(
        ProjectWorkloadKind workload,
        IReadOnlyList<ProjectTargetDefinition> targets)
    {
        if (targets.Count == 0)
        {
            return [];
        }

        return targets
            .SelectMany(target => new[]
            {
                CreateLaunchProfile(target, ProjectLaunchMode.Run),
                CreateLaunchProfile(target, ProjectLaunchMode.Debug)
            })
            .ToArray();
    }

    private static ProjectLaunchProfile CreateLaunchProfile(ProjectTargetDefinition target, ProjectLaunchMode mode)
    {
        var modeLabel = mode == ProjectLaunchMode.Debug ? "Debug" : "Run";
        var requiresVsCode = mode == ProjectLaunchMode.Debug && target.RequiresVsCodeForDebugging;
        var requiresEmulator = target.Platform == ApplicationTargetPlatform.Android;
        var requiresSimulator = target.Platform == ApplicationTargetPlatform.iOS;

        return new ProjectLaunchProfile
        {
            Id = $"{target.Workload.ToString().ToLowerInvariant()}-{target.Platform.ToString().ToLowerInvariant()}-{mode.ToString().ToLowerInvariant()}",
            DisplayName = $"{target.DisplayName} {modeLabel}",
            Platform = target.Platform,
            Mode = mode,
            PreferredMachineRole = RunnerMachineRole.Worker,
            BuildCommand = "dotnet build",
            LaunchCommand = BuildLaunchCommand(target),
            DebugCommand = requiresVsCode ? "code ." : null,
            RequiresVsCode = requiresVsCode,
            RequiresEmulator = requiresEmulator,
            RequiresSimulator = requiresSimulator,
            Notes = BuildLaunchNotes(target, mode, requiresEmulator, requiresSimulator)
        };
    }

    private static string BuildLaunchCommand(ProjectTargetDefinition target) => target.Workload switch
    {
        ProjectWorkloadKind.Blazor => "dotnet run",
        ProjectWorkloadKind.Maui => "dotnet build -t:Run",
        _ => "dotnet run"
    };

    private static string? BuildLaunchNotes(
        ProjectTargetDefinition target,
        ProjectLaunchMode mode,
        bool requiresEmulator,
        bool requiresSimulator)
    {
        var notes = new List<string>();

        if (!string.IsNullOrWhiteSpace(target.PackageRequirement))
        {
            notes.Add($"Package requirement: {target.PackageRequirement}.");
        }

        if (mode == ProjectLaunchMode.Debug && target.RequiresVsCodeForDebugging)
        {
            notes.Add("Debug sessions should be launched through VS Code.");
        }

        if (requiresEmulator)
        {
            notes.Add("Select an Android emulator profile before launch.");
        }

        if (requiresSimulator)
        {
            notes.Add("Select an Apple simulator profile before launch.");
        }

        if (!string.IsNullOrWhiteSpace(target.Notes))
        {
            notes.Add(target.Notes);
        }

        return notes.Count == 0 ? null : string.Join(" ", notes);
    }
}
