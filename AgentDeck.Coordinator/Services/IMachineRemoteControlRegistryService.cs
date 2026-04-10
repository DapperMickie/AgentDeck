using AgentDeck.Shared.Models;

namespace AgentDeck.Coordinator.Services;

public interface IMachineRemoteControlRegistryService
{
    SemaphoreSlim GetGate(string machineId);
    MachineRemoteControlState? GetState(string machineId);
    MachineRemoteControlState SetState(MachineRemoteControlState state);
    MachineRemoteControlState? ClearState(string machineId, string? viewerSessionId = null);
}
