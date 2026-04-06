using AgentDeck.Core.Models;
using AgentDeck.Shared.Models;

namespace AgentDeck.Core.Services;

/// <summary>Routes companion app requests to the correct runner machine connection.</summary>
public interface IRunnerConnectionManager
{
    event EventHandler<RunnerMachineConnectionChangedEventArgs>? ConnectionStateChanged;
    event EventHandler<TerminalOutput>? OutputReceived;
    event EventHandler<TerminalSession>? SessionCreated;
    event EventHandler<TerminalSession>? SessionUpdated;
    event EventHandler<string>? SessionClosed;

    HubConnectionState GetConnectionState(string machineId);
    int ConnectedMachineCount { get; }

    Task ConnectAsync(RunnerMachineSettings machine, CancellationToken cancellationToken = default);
    Task DisconnectAsync(string machineId);
    Task JoinSessionAsync(string sessionId);
    Task LeaveSessionAsync(string sessionId);
    Task SendInputAsync(string sessionId, string data);
    Task ResizeTerminalAsync(string sessionId, int cols, int rows);
    Task<TerminalSession?> CreateSessionAsync(RunnerMachineSettings machine, CreateTerminalRequest request, CancellationToken cancellationToken = default);
    Task CloseSessionAsync(string sessionId);
    Task<IReadOnlyList<TerminalSession>> GetSessionsAsync(RunnerMachineSettings machine, CancellationToken cancellationToken = default);
    Task<WorkspaceInfo?> GetWorkspaceAsync(RunnerMachineSettings machine, CancellationToken cancellationToken = default);
    Task<MachineCapabilitiesSnapshot?> GetMachineCapabilitiesAsync(RunnerMachineSettings machine, CancellationToken cancellationToken = default);
}
