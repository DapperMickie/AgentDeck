using AgentDeck.Shared.Enums;

namespace AgentDeck.Core.Models;

/// <summary>Named runner machine profile managed by the companion app.</summary>
public sealed class RunnerMachineSettings
{
    /// <summary>Stable identifier for the machine profile.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>User-facing machine name.</summary>
    public string Name { get; set; } = "Runner machine";

    /// <summary>Intended orchestration role for this machine profile.</summary>
    public RunnerMachineRole Role { get; set; } = RunnerMachineRole.Standalone;

    /// <summary>Advertised base URL of the runner, used by the coordinator when brokering requests.</summary>
    public string RunnerUrl { get; set; } = string.Empty;

    /// <summary>Whether this machine should connect automatically when the app starts.</summary>
    public bool AutoConnect { get; set; }

    public RunnerMachineSettings Clone()
    {
        return new RunnerMachineSettings
        {
            Id = Id,
            Name = Name,
            Role = Role,
            RunnerUrl = RunnerUrl,
            AutoConnect = AutoConnect
        };
    }
}
