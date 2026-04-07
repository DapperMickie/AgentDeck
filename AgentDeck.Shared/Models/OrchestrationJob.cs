using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Durable run/debug orchestration record tracked separately from terminal sessions.</summary>
public sealed class OrchestrationJob
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public required string LaunchProfileId { get; init; }
    public required string LaunchProfileName { get; init; }
    public ApplicationTargetPlatform Platform { get; init; }
    public ProjectLaunchMode Mode { get; init; }
    public ProjectLaunchDriver LaunchDriver { get; init; }
    public RunnerMachineRole TargetMachineRole { get; init; } = RunnerMachineRole.Worker;
    public string? TargetMachineId { get; init; }
    public string? TargetMachineName { get; init; }
    public string WorkingDirectory { get; init; } = string.Empty;
    public string BuildCommand { get; init; } = string.Empty;
    public string? LaunchCommand { get; init; }
    public string? BootstrapCommand { get; init; }
    public string? DebugConfigurationName { get; init; }
    public OrchestrationJobStatus Status { get; set; } = OrchestrationJobStatus.Queued;
    public string? SessionId { get; set; }
    public int? ExitCode { get; set; }
    public string? StatusMessage { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<OrchestrationJobStep> Steps { get; set; } = [];
    public IReadOnlyList<OrchestrationJobLogEntry> Logs { get; set; } = [];
}
