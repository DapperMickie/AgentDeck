using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Worker-to-coordinator registration payload.</summary>
public sealed class RegisterRunnerMachineRequest
{
    public required string MachineId { get; init; }
    public required string MachineName { get; init; }
    public RunnerMachineRole Role { get; init; } = RunnerMachineRole.Worker;
    public string? RunnerUrl { get; init; }
    public MachinePlatformProfile? Platform { get; init; }
    public IReadOnlyList<MachineTargetSupport> SupportedTargets { get; init; } = [];
}
