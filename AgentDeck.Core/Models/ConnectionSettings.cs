namespace AgentDeck.Core.Models;

/// <summary>User-configured connection settings for the AgentDeck runner.</summary>
public sealed class ConnectionSettings
{
    /// <summary>Base URL of the runner (e.g. http://192.168.1.5:5000).</summary>
    public string RunnerUrl { get; set; } = "http://localhost:5000";

    /// <summary>Whether to automatically connect to the runner on app start.</summary>
    public bool AutoConnect { get; set; } = true;

    /// <summary>Selected workload definition used when generating runner container commands.</summary>
    public string? SelectedWorkloadId { get; set; }

    /// <summary>Host path mounted into the runner container as the workspace root.</summary>
    public string DockerWorkspacePath { get; set; } = string.Empty;

    /// <summary>Local repository path that contains AgentDeck.Runner/Dockerfile for base image builds.</summary>
    public string RunnerSourcePath { get; set; } = string.Empty;
}
