namespace AgentDeck.Core.Models;

/// <summary>Resolved Docker commands for building and running a workload container.</summary>
public sealed class WorkloadContainerCommandSet
{
    public required string BaseImageTag { get; init; }
    public required string WorkloadImageTag { get; init; }
    public required string ContainerName { get; init; }
    public string? BuildBaseImageCommand { get; init; }
    public required string GeneratedDockerfile { get; init; }
    public required string BuildWorkloadImageCommand { get; init; }
    public required string StartContainerCommand { get; init; }
    public required string StopContainerCommand { get; init; }
}
