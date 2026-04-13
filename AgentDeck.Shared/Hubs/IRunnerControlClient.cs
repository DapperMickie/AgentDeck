using AgentDeck.Shared.Models;

namespace AgentDeck.Shared.Hubs;

public interface IRunnerControlClient
{
    Task<TerminalSession> CreateSessionAsync(CreateTerminalRequest request);
    Task CloseSessionAsync(string sessionId);
    Task<IReadOnlyList<TerminalSession>> GetSessionsAsync();
    Task SendInputAsync(string sessionId, string data);
    Task ResizeTerminalAsync(string sessionId, int cols, int rows);

    Task<WorkspaceInfo?> GetWorkspaceAsync();
    Task<OpenProjectOnRunnerResult?> OpenProjectAsync(OpenProjectOnRunnerRequest request, string actorId);
    Task<MachineCapabilitiesSnapshot?> GetMachineCapabilitiesAsync();
    Task<MachineCapabilityInstallResult?> InstallMachineCapabilityAsync(string capabilityId, MachineCapabilityInstallRequest request, string actorId);
    Task<MachineCapabilityInstallResult?> UpdateMachineCapabilityAsync(string capabilityId, string actorId);
    Task RetryMachineWorkflowPackAsync(string actorId);

    Task<IReadOnlyList<OrchestrationJob>> GetOrchestrationJobsAsync();
    Task<OrchestrationJob?> QueueOrchestrationJobAsync(CreateOrchestrationJobRequest request, string actorId);
    Task<OrchestrationJob?> CancelOrchestrationJobAsync(string jobId, string actorId);

    Task<IReadOnlyList<RemoteViewerSession>> GetViewerSessionsAsync();
    Task<RemoteViewerSession> CreateViewerSessionAsync(CreateRemoteViewerSessionRequest request, string actorId);
    Task<RemoteViewerSession?> CloseViewerSessionAsync(string viewerSessionId, string actorId);
    Task SendViewerPointerInputAsync(string viewerSessionId, string actorId, RemoteViewerPointerInputEvent input);
    Task SendViewerKeyboardInputAsync(string viewerSessionId, string actorId, RemoteViewerKeyboardInputEvent input);

    Task<IReadOnlyList<VirtualDeviceCatalogSnapshot>> GetVirtualDeviceCatalogsAsync();
    Task<VirtualDeviceLaunchResolution?> ResolveVirtualDeviceAsync(VirtualDeviceLaunchSelection selection);
}
