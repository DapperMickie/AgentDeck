using AgentDeck.Shared.Models;

namespace AgentDeck.Coordinator.Services;

public interface IRunnerBrokerService
{
    Task<TerminalSession> CreateSessionAsync(string machineId, CreateTerminalRequest request, CancellationToken cancellationToken = default);
    Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TerminalSession>> GetSessionsAsync(string machineId, CancellationToken cancellationToken = default);
    Task JoinSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task LeaveSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task ResizeTerminalAsync(string sessionId, int cols, int rows, CancellationToken cancellationToken = default);
    Task SendInputAsync(string sessionId, string data, CancellationToken cancellationToken = default);
    Task<WorkspaceInfo?> GetWorkspaceAsync(string machineId, CancellationToken cancellationToken = default);
    Task<OpenProjectOnRunnerResult?> OpenProjectAsync(string machineId, OpenProjectOnRunnerRequest request, string actorId, CancellationToken cancellationToken = default);
    Task<MachineCapabilitiesSnapshot?> GetMachineCapabilitiesAsync(string machineId, CancellationToken cancellationToken = default);
    Task<MachineCapabilityInstallResult?> InstallMachineCapabilityAsync(string machineId, string capabilityId, MachineCapabilityInstallRequest request, string actorId, CancellationToken cancellationToken = default);
    Task<MachineCapabilityInstallResult?> UpdateMachineCapabilityAsync(string machineId, string capabilityId, string actorId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OrchestrationJob>> GetOrchestrationJobsAsync(string machineId, CancellationToken cancellationToken = default);
    Task<OrchestrationJob?> QueueOrchestrationJobAsync(string machineId, CreateOrchestrationJobRequest request, string actorId, CancellationToken cancellationToken = default);
    Task<OrchestrationJob?> CancelOrchestrationJobAsync(string machineId, string jobId, string actorId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RemoteViewerSession>> GetViewerSessionsAsync(string machineId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VirtualDeviceCatalogSnapshot>> GetVirtualDeviceCatalogsAsync(string machineId, CancellationToken cancellationToken = default);
    Task<VirtualDeviceLaunchResolution?> ResolveVirtualDeviceAsync(string machineId, VirtualDeviceLaunchSelection selection, CancellationToken cancellationToken = default);
}
