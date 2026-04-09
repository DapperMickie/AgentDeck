using System.Collections.Concurrent;
using AgentDeck.Coordinator.Configuration;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;
using Microsoft.Extensions.Options;

namespace AgentDeck.Coordinator.Services;

public sealed class WorkerRegistryService : IWorkerRegistryService
{
    private sealed record WorkerEntry(RegisteredRunnerMachine Machine);

    private readonly Lock _lock = new();
    private readonly CoordinatorOptions _coordinatorOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, WorkerEntry> _workers = new(StringComparer.OrdinalIgnoreCase);

    public WorkerRegistryService(
        IOptions<CoordinatorOptions> coordinatorOptions,
        TimeProvider timeProvider)
    {
        _coordinatorOptions = coordinatorOptions.Value;
        _timeProvider = timeProvider;
    }

    public Task<IReadOnlyList<RegisteredRunnerMachine>> GetMachinesAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<RegisteredRunnerMachine> machines = RefreshWorkerStates()
                .OrderBy(machine => machine.MachineName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return Task.FromResult(machines);
        }
    }

    public RegisterRunnerMachineResponse RegisterOrUpdateWorker(RegisterRunnerMachineRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MachineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MachineName);

        lock (_lock)
        {
            var now = _timeProvider.GetUtcNow();
            var machineId = Normalize(request.MachineId);
            var existing = _workers.TryGetValue(machineId, out var current) ? current.Machine : null;
            var machine = new RegisteredRunnerMachine
            {
                MachineId = machineId,
                MachineName = Normalize(request.MachineName),
                Role = RunnerMachineRole.Worker,
                RunnerUrl = string.IsNullOrWhiteSpace(request.RunnerUrl) ? null : request.RunnerUrl.Trim(),
                RegisteredAt = existing?.RegisteredAt ?? now,
                LastSeenAt = now,
                IsOnline = true,
                IsCoordinator = false,
                Platform = request.Platform,
                SupportedTargets = request.SupportedTargets
                    .Where(target => !string.IsNullOrWhiteSpace(target.DisplayName))
                    .ToArray()
            };

            _workers[machineId] = new WorkerEntry(machine);
            return new RegisterRunnerMachineResponse
            {
                Machine = machine,
                HeartbeatInterval = _coordinatorOptions.WorkerHeartbeatInterval
            };
        }
    }

    private IReadOnlyList<RegisteredRunnerMachine> RefreshWorkerStates()
    {
        var now = _timeProvider.GetUtcNow();
        var expiryThreshold = _coordinatorOptions.WorkerExpiry > TimeSpan.Zero
            ? now - _coordinatorOptions.WorkerExpiry
            : DateTimeOffset.MinValue;
        var onlineThreshold = _coordinatorOptions.WorkerHeartbeatInterval > TimeSpan.Zero
            ? now - _coordinatorOptions.WorkerHeartbeatInterval
            : DateTimeOffset.MinValue;
        var machines = new List<RegisteredRunnerMachine>();

        foreach (var worker in _workers.ToArray())
        {
            var machine = worker.Value.Machine;
            if (machine.LastSeenAt < expiryThreshold)
            {
                _workers.TryRemove(worker.Key, out _);
                continue;
            }

            var isOnline = machine.LastSeenAt >= onlineThreshold;
            if (machine.IsOnline != isOnline)
            {
                machine = new RegisteredRunnerMachine
                {
                    MachineId = machine.MachineId,
                    MachineName = machine.MachineName,
                    Role = machine.Role,
                    RunnerUrl = machine.RunnerUrl,
                    RegisteredAt = machine.RegisteredAt,
                    LastSeenAt = machine.LastSeenAt,
                    IsOnline = isOnline,
                    IsCoordinator = machine.IsCoordinator,
                    Platform = machine.Platform,
                    SupportedTargets = machine.SupportedTargets
                };
                _workers[worker.Key] = new WorkerEntry(machine);
            }

            machines.Add(machine);
        }

        return machines;
    }

    private static string Normalize(string value) => value.Trim();
}
