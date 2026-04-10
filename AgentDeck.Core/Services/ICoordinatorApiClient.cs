using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

namespace AgentDeck.Core.Services;

/// <summary>Queries the central coordinator API over HTTP.</summary>
public interface ICoordinatorApiClient
{
    Task<bool> CheckHealthAsync(string coordinatorUrl, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RegisteredRunnerMachine>> GetMachinesAsync(string coordinatorUrl, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectDefinition>> GetProjectsAsync(string coordinatorUrl, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectSessionRecord>> GetProjectSessionsAsync(string coordinatorUrl, string? projectId = null, CancellationToken cancellationToken = default);

    Task<OpenProjectOnMachineResult?> OpenProjectOnMachineAsync(string coordinatorUrl, string projectId, string machineId, CancellationToken cancellationToken = default);

    Task<ProjectSessionRecord?> AttachProjectSessionAsync(string coordinatorUrl, string projectSessionId, CancellationToken cancellationToken = default);

    Task<ProjectSessionRecord?> DetachProjectSessionAsync(string coordinatorUrl, string projectSessionId, CancellationToken cancellationToken = default);

    Task<ProjectSessionRecord?> UpdateProjectSessionControlAsync(string coordinatorUrl, string projectSessionId, UpdateProjectSessionControlRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrchestrationJob>> GetMachineOrchestrationJobsAsync(string coordinatorUrl, string machineId, CancellationToken cancellationToken = default);

    Task<OrchestrationJob?> QueueMachineOrchestrationJobAsync(string coordinatorUrl, string machineId, CreateOrchestrationJobRequest request, CancellationToken cancellationToken = default);

    Task<OrchestrationJob?> CancelMachineOrchestrationJobAsync(string coordinatorUrl, string machineId, string jobId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RemoteViewerSession>> GetMachineViewerSessionsAsync(string coordinatorUrl, string machineId, CancellationToken cancellationToken = default);

    Task<MachineRemoteControlState?> GetMachineRemoteControlStateAsync(string coordinatorUrl, string machineId, CancellationToken cancellationToken = default);

    Task<RemoteViewerSession?> CreateMachineViewerSessionAsync(string coordinatorUrl, string machineId, CreateMachineViewerSessionRequest request, CancellationToken cancellationToken = default);

    Task UpdateMachineViewerControlAsync(string coordinatorUrl, string machineId, string viewerSessionId, ProjectSessionControlRequestMode mode, CancellationToken cancellationToken = default);

    Task<RemoteViewerSession?> CloseMachineViewerSessionAsync(string coordinatorUrl, string machineId, string viewerSessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VirtualDeviceCatalogSnapshot>> GetMachineVirtualDeviceCatalogsAsync(string coordinatorUrl, string machineId, CancellationToken cancellationToken = default);

    Task<VirtualDeviceLaunchResolution?> ResolveMachineVirtualDeviceAsync(string coordinatorUrl, string machineId, VirtualDeviceLaunchSelection selection, CancellationToken cancellationToken = default);
}
