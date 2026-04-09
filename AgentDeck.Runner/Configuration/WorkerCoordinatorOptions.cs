using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Configuration;

/// <summary>Configuration for registering a runner outward to the central coordinator API.</summary>
public sealed class WorkerCoordinatorOptions
{
    public const string SectionName = "Coordinator";

    /// <summary>Stable identifier this runner advertises to the coordinator.</summary>
    public string MachineId { get; set; } = Environment.MachineName;

    /// <summary>User-facing machine name advertised to the coordinator and companion app.</summary>
    public string MachineName { get; set; } = Environment.MachineName;

    /// <summary>Coordinator base URL that worker runners register with.</summary>
    public string? CoordinatorUrl { get; set; }

    /// <summary>Optional runner URL the worker advertises back to the coordinator for future dispatch.</summary>
    public string? AdvertisedRunnerUrl { get; set; }

    /// <summary>Protocol version this runner uses when talking to the coordinator.</summary>
    public int ProtocolVersion { get; set; } = 1;

    /// <summary>When true, download the payload referenced by the desired update manifest into the staging directory.</summary>
    public bool DownloadUpdatePayload { get; set; }

    /// <summary>Optional local root for staged update manifests and payloads.</summary>
    public string? UpdateStagingRoot { get; set; }

    /// <summary>Allow plain HTTP only when the configured coordinator resolves to a loopback host.</summary>
    public bool AllowInsecureHttpCoordinatorForLoopback { get; set; } = true;

    /// <summary>Development-only override that also allows plain HTTP for non-loopback coordinators.</summary>
    public bool AllowInsecureHttpCoordinatorForDevelopment { get; set; }

    /// <summary>Trusted public keys used to verify signed update manifests.</summary>
    public IReadOnlyList<RunnerTrustedManifestSigner> TrustedManifestSigners { get; set; } = [];

    /// <summary>Optional local root for extracted candidate installs created by the apply flow.</summary>
    public string? UpdateApplyRoot { get; set; }

    /// <summary>How long the detached apply helper waits for the current runner process to exit before failing.</summary>
    public TimeSpan UpdateApplyProcessExitTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Default interval workers use when refreshing coordinator registration.</summary>
    public TimeSpan WorkerHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);
}
