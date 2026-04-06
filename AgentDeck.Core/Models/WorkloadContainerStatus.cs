namespace AgentDeck.Core.Models;

/// <summary>Observed local Docker state for a selected workload.</summary>
public sealed class WorkloadContainerStatus
{
    public bool DockerAvailable { get; init; }
    public string DockerVersion { get; init; } = string.Empty;
    public bool BaseImageExists { get; init; }
    public bool WorkloadImageExists { get; init; }
    public bool ContainerExists { get; init; }
    public string ContainerState { get; init; } = "not-created";
    public string ContainerId { get; init; } = string.Empty;
    public string ErrorMessage { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, bool> AuthVolumeExists { get; init; } = new Dictionary<string, bool>();
    public IReadOnlyDictionary<string, bool> CacheVolumeExists { get; init; } = new Dictionary<string, bool>();
    public IReadOnlyDictionary<string, bool> SecretAvailability { get; init; } = new Dictionary<string, bool>();
}
