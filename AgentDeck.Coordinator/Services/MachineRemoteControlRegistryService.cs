using System.Collections.Concurrent;
using AgentDeck.Shared.Models;

namespace AgentDeck.Coordinator.Services;

public sealed class MachineRemoteControlRegistryService : IMachineRemoteControlRegistryService
{
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MachineRemoteControlState> _states = new(StringComparer.OrdinalIgnoreCase);

    public SemaphoreSlim GetGate(string machineId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        return _gates.GetOrAdd(machineId.Trim(), static _ => new SemaphoreSlim(1, 1));
    }

    public MachineRemoteControlState? GetState(string machineId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);

        lock (_lock)
        {
            return _states.TryGetValue(machineId.Trim(), out var state)
                ? Clone(state)
                : null;
        }
    }

    public MachineRemoteControlState SetState(MachineRemoteControlState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var normalizedMachineId = state.MachineId.Trim();
        var normalizedState = new MachineRemoteControlState
        {
            MachineName = state.MachineName,
            ControllerCompanionId = state.ControllerCompanionId.Trim(),
            ControllerDisplayName = state.ControllerDisplayName,
            ViewerSessionId = state.ViewerSessionId.Trim(),
            TargetKind = state.TargetKind,
            TargetDisplayName = state.TargetDisplayName,
            Provider = state.Provider,
            ViewerStatus = state.ViewerStatus,
            ConnectionUri = state.ConnectionUri,
            StatusMessage = state.StatusMessage,
            AcquiredAt = state.AcquiredAt,
            UpdatedAt = state.UpdatedAt,
            MachineId = normalizedMachineId
        };

        lock (_lock)
        {
            _states[normalizedMachineId] = normalizedState;
            return Clone(normalizedState);
        }
    }

    public MachineRemoteControlState? ClearState(string machineId, string? viewerSessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);

        lock (_lock)
        {
            var normalizedMachineId = machineId.Trim();
            if (!_states.TryGetValue(normalizedMachineId, out var existing))
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(viewerSessionId) &&
                !string.Equals(existing.ViewerSessionId, viewerSessionId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Clone(existing);
            }

            _states.Remove(normalizedMachineId);
            return null;
        }
    }

    private static MachineRemoteControlState Clone(MachineRemoteControlState state) =>
        new()
        {
            MachineId = state.MachineId,
            MachineName = state.MachineName,
            ControllerCompanionId = state.ControllerCompanionId,
            ControllerDisplayName = state.ControllerDisplayName,
            ViewerSessionId = state.ViewerSessionId,
            TargetKind = state.TargetKind,
            TargetDisplayName = state.TargetDisplayName,
            Provider = state.Provider,
            ViewerStatus = state.ViewerStatus,
            ConnectionUri = state.ConnectionUri,
            StatusMessage = state.StatusMessage,
            AcquiredAt = state.AcquiredAt,
            UpdatedAt = state.UpdatedAt
        };
}
