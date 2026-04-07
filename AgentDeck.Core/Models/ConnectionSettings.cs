using AgentDeck.Shared.Enums;

namespace AgentDeck.Core.Models;

/// <summary>User-configured runner machine profiles for the companion app.</summary>
public sealed class ConnectionSettings
{
    /// <summary>Configured runner machines.</summary>
    public List<RunnerMachineSettings> Machines { get; set; } = [CreateLocalMachine()];

    /// <summary>Machine selected by default when creating a new terminal.</summary>
    public string? PreferredMachineId { get; set; } = LocalMachineId;

    public const string LocalMachineId = "local-machine";

    public static ConnectionSettings CreateDefault()
    {
        return new ConnectionSettings
        {
            Machines = [CreateLocalMachine()],
            PreferredMachineId = LocalMachineId
        };
    }

    public RunnerMachineSettings? FindMachine(string? machineId)
    {
        if (Machines.Count == 0)
        {
            Machines.Add(CreateLocalMachine());
        }

        if (!string.IsNullOrWhiteSpace(machineId))
        {
            var exactMatch = Machines.FirstOrDefault(m => string.Equals(m.Id, machineId, StringComparison.OrdinalIgnoreCase));
            if (exactMatch is not null)
            {
                return exactMatch;
            }
        }

        var preferred = Machines.FirstOrDefault(m => string.Equals(m.Id, PreferredMachineId, StringComparison.OrdinalIgnoreCase));
        return preferred ?? Machines[0];
    }

    public void Normalize()
    {
        Machines ??= [];

        if (Machines.Count == 0)
        {
            Machines.Add(CreateLocalMachine());
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < Machines.Count; index++)
        {
            var machine = Machines[index] ?? new RunnerMachineSettings();
            machine.Id = string.IsNullOrWhiteSpace(machine.Id) ? Guid.NewGuid().ToString("n") : machine.Id.Trim();
            machine.Name = string.IsNullOrWhiteSpace(machine.Name) ? $"Machine {index + 1}" : machine.Name.Trim();
            machine.RunnerUrl = string.IsNullOrWhiteSpace(machine.RunnerUrl) ? "http://localhost:5000" : machine.RunnerUrl.Trim();

            while (!seenIds.Add(machine.Id))
            {
                machine.Id = Guid.NewGuid().ToString("n");
            }

            Machines[index] = machine;
        }

        if (string.IsNullOrWhiteSpace(PreferredMachineId) ||
            Machines.All(m => !string.Equals(m.Id, PreferredMachineId, StringComparison.OrdinalIgnoreCase)))
        {
            PreferredMachineId = Machines[0].Id;
        }
    }

    public static RunnerMachineSettings CreateLocalMachine()
    {
        return new RunnerMachineSettings
        {
            Id = LocalMachineId,
            Name = "Local machine",
            Role = RunnerMachineRole.Standalone,
            RunnerUrl = "http://localhost:5000",
            AutoConnect = true
        };
    }
}
