using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Launch contract describing how to run or debug a specific project target.</summary>
public sealed class ProjectLaunchProfile
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public ApplicationTargetPlatform Platform { get; init; }
    public ProjectLaunchMode Mode { get; init; }
    public ProjectLaunchDriver LaunchDriver { get; init; } = ProjectLaunchDriver.DirectCommand;
    public RunnerMachineRole PreferredMachineRole { get; init; } = RunnerMachineRole.Worker;
    public string? PreferredMachineId { get; init; }
    public string BuildCommand { get; init; } = string.Empty;
    public string? LaunchCommand { get; init; }
    public string? BootstrapCommand { get; init; }
    public string? DebugConfigurationName { get; init; }
    public bool RequiresVsCode { get; init; }
    public bool RequiresEmulator { get; init; }
    public bool RequiresSimulator { get; init; }
    public string? DeviceProfile { get; init; }
    public string? Notes { get; init; }
}
