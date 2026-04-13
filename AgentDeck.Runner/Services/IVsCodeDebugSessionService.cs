namespace AgentDeck.Runner.Services;

/// <summary>Launches and tracks VS Code-backed debug sessions for orchestration jobs.</summary>
public interface IVsCodeDebugSessionService
{
    IReadOnlyList<AgentDeck.Shared.Models.VsCodeDebugSession> GetAll();
    AgentDeck.Shared.Models.VsCodeDebugSession? Get(string orchestrationSessionId);
    Task<VsCodeDebugLaunchResult> LaunchAsync(string orchestrationSessionId, AgentDeck.Shared.Models.OrchestrationJob job, string workingDirectory, CancellationToken cancellationToken = default);
    Task NotifyHostStartedAsync(string orchestrationSessionId, int processId, CancellationToken cancellationToken = default);
    Task NotifyHostReadyAsync(string orchestrationSessionId, string projectName, CancellationToken cancellationToken = default);
    Task CompleteAsync(string orchestrationSessionId, int exitCode, CancellationToken cancellationToken = default);
    Task StopAsync(string orchestrationSessionId, CancellationToken cancellationToken = default);
}
