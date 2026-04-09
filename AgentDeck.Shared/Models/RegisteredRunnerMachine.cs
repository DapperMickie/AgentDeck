using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Coordinator-visible machine record for local and worker runners.</summary>
public sealed class RegisteredRunnerMachine
{
    public required string MachineId { get; init; }
    public required string MachineName { get; init; }
    public RunnerMachineRole Role { get; init; } = RunnerMachineRole.Standalone;
    public string? RunnerUrl { get; init; }
    public DateTimeOffset RegisteredAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; init; } = DateTimeOffset.UtcNow;
    public bool IsOnline { get; init; } = true;
    public bool IsCoordinator { get; init; }
    public MachinePlatformProfile? Platform { get; init; }
    public IReadOnlyList<MachineTargetSupport> SupportedTargets { get; init; } = [];
}
