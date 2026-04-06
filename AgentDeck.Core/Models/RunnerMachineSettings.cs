namespace AgentDeck.Core.Models;

/// <summary>Named runner machine profile managed by the companion app.</summary>
public sealed class RunnerMachineSettings
{
    /// <summary>Stable identifier for the machine profile.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>User-facing machine name.</summary>
    public string Name { get; set; } = "Local machine";

    /// <summary>Base URL of the runner (e.g. http://192.168.1.5:5000).</summary>
    public string RunnerUrl { get; set; } = "http://localhost:5000";

    /// <summary>Whether this machine should connect automatically when the app starts.</summary>
    public bool AutoConnect { get; set; } = true;

    public RunnerMachineSettings Clone()
    {
        return new RunnerMachineSettings
        {
            Id = Id,
            Name = Name,
            RunnerUrl = RunnerUrl,
            AutoConnect = AutoConnect
        };
    }
}
