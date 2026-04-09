namespace AgentDeck.Shared.Models;

/// <summary>Coordinator response to worker registration.</summary>
public sealed class RegisterRunnerMachineResponse
{
    public required RegisteredRunnerMachine Machine { get; init; }
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);
}
