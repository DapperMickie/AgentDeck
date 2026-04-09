using AgentDeck.Shared.Enums;

namespace AgentDeck.Core.Models;

/// <summary>User-configured runner machine profiles for the companion app.</summary>
public sealed class ConnectionSettings
{
    /// <summary>Base URL of the central coordinator API.</summary>
    public string CoordinatorUrl { get; set; } = string.Empty;

    /// <summary>Configured runner machines.</summary>
    public List<RunnerMachineSettings> Machines { get; set; } = [CreateMachineTemplate()];

    /// <summary>Machine selected by default when creating a new terminal.</summary>
    public string? PreferredMachineId { get; set; } = LocalMachineId;

    public const string LocalMachineId = "local-machine";

    public static ConnectionSettings CreateDefault()
    {
        return new ConnectionSettings
        {
            CoordinatorUrl = string.Empty,
            Machines = [CreateMachineTemplate()],
            PreferredMachineId = LocalMachineId
        };
    }

    public RunnerMachineSettings? FindMachine(string? machineId)
    {
        if (Machines.Count == 0)
        {
            Machines.Add(CreateMachineTemplate());
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
        CoordinatorUrl = string.IsNullOrWhiteSpace(CoordinatorUrl)
            ? string.Empty
            : CoordinatorUrl.Trim();

        Machines ??= [];

        if (Machines.Count == 0)
        {
            Machines.Add(CreateMachineTemplate());
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < Machines.Count; index++)
        {
            var machine = Machines[index] ?? new RunnerMachineSettings();
            machine.Id = string.IsNullOrWhiteSpace(machine.Id) ? Guid.NewGuid().ToString("n") : machine.Id.Trim();
            machine.Name = string.IsNullOrWhiteSpace(machine.Name)
                ? GetDefaultMachineName(index)
                : machine.Name.Trim();
            machine.RunnerUrl = string.IsNullOrWhiteSpace(machine.RunnerUrl)
                ? string.Empty
                : machine.RunnerUrl.Trim();
            if (machine.Role == RunnerMachineRole.Coordinator)
            {
                machine.Role = RunnerMachineRole.Standalone;
            }

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
        => CreateMachineTemplate();

    public static RunnerMachineSettings CreateMachineTemplate()
    {
        return new RunnerMachineSettings
        {
            Id = LocalMachineId,
            Name = GetDefaultMachineName(0),
            Role = RunnerMachineRole.Standalone,
            RunnerUrl = string.Empty,
            AutoConnect = false
        };
    }

    private static string GetDefaultMachineName(int index) =>
        index == 0 ? "Runner machine" : $"Runner machine {index + 1}";
}
