using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

namespace AgentDeck.Core.Models;

public sealed class CompanionDashboardState
{
    public string CoordinatorUrl { get; init; } = string.Empty;
    public bool CoordinatorConfigured { get; init; }
    public bool CoordinatorReachable { get; init; }
    public string? CoordinatorErrorMessage { get; init; }
    public IReadOnlyList<CompanionMachineSummary> Machines { get; init; } = [];
    public IReadOnlyList<CompanionProjectSummary> Projects { get; init; } = [];
    public IReadOnlyList<ProjectSessionRecord> ProjectSessions { get; init; } = [];
    public IReadOnlyList<CompanionViewerSurfaceSummary> ViewerSurfaces { get; init; } = [];
    public IReadOnlyList<TerminalSession> ActiveSessions { get; init; } = [];
}

public sealed class CompanionMachineSummary
{
    public string MachineId { get; init; } = string.Empty;
    public string MachineName { get; init; } = string.Empty;
    public RunnerMachineRole Role { get; init; } = RunnerMachineRole.Standalone;
    public bool IsOnline { get; init; }
    public bool IsConnected { get; init; }
    public RunnerHostPlatform HostPlatform { get; init; } = RunnerHostPlatform.Unknown;
    public string? AgentVersion { get; init; }
    public DateTimeOffset LastSeenAt { get; init; }
    public IReadOnlyList<MachineTargetSupport> SupportedTargets { get; init; } = [];
}

public sealed class CompanionProjectSummary
{
    public ProjectDefinition Definition { get; init; } = new();
    public IReadOnlyList<CompanionProjectTargetSummary> Targets { get; init; } = [];
}

public sealed class CompanionProjectTargetSummary
{
    public ApplicationTargetPlatform Platform { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public IReadOnlyList<ProjectLaunchProfile> LaunchProfiles { get; init; } = [];
    public IReadOnlyList<string> ReadyMachineNames { get; init; } = [];
    public IReadOnlyList<string> SetupRequiredMachineNames { get; init; } = [];
}

public sealed class CompanionViewerSurfaceSummary
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Availability { get; init; } = string.Empty;
}
