using AgentDeck.Core.Services;

namespace AgentDeck.Core.Models;

/// <summary>Connection-state change notification for a specific machine.</summary>
public sealed class RunnerMachineConnectionChangedEventArgs : EventArgs
{
    public required string MachineId { get; init; }
    public required string MachineName { get; init; }
    public required HubConnectionState State { get; init; }
}
