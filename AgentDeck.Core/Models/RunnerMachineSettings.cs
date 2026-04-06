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

    /// <summary>Selected workload definition used when generating runner container commands.</summary>
    public string? SelectedWorkloadId { get; set; }

    /// <summary>Host path mounted into the runner container as the workspace root.</summary>
    public string DockerWorkspacePath { get; set; } = string.Empty;

    /// <summary>Local repository path that contains AgentDeck.Runner/Dockerfile for base image builds.</summary>
    public string RunnerSourcePath { get; set; } = string.Empty;

    public RunnerMachineSettings Clone()
    {
        return new RunnerMachineSettings
        {
            Id = Id,
            Name = Name,
            RunnerUrl = RunnerUrl,
            AutoConnect = AutoConnect,
            SelectedWorkloadId = SelectedWorkloadId,
            DockerWorkspacePath = DockerWorkspacePath,
            RunnerSourcePath = RunnerSourcePath
        };
    }
}
