using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Request payload used to queue a coordinator-managed run or debug job.</summary>
public sealed class CreateOrchestrationJobRequest
{
    public required string ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public required string LaunchProfileId { get; init; }
    public required string LaunchProfileName { get; init; }
    public required string WorkingDirectory { get; init; }
    public ApplicationTargetPlatform Platform { get; init; }
    public ProjectLaunchMode Mode { get; init; }
    public ProjectLaunchDriver LaunchDriver { get; init; } = ProjectLaunchDriver.DirectCommand;
    public RunnerMachineRole TargetMachineRole { get; init; } = RunnerMachineRole.Worker;
    public string? TargetMachineId { get; init; }
    public string? TargetMachineName { get; init; }
    public string BuildCommand { get; init; } = string.Empty;
    public string? LaunchCommand { get; init; }
    public string? BootstrapCommand { get; init; }
    public string? DebugConfigurationName { get; init; }
    public VirtualDeviceLaunchSelection? DeviceSelection { get; init; }
}
