using RdpPoc.Contracts;

namespace AgentDeck.Runner.Services;

public interface IRunnerLaunchedApplicationService
{
    void TrackViewerSession(string viewerSessionId, string displayName, string? terminalSessionId = null);

    void UpdateTrackedProcess(string viewerSessionId, int processId, string? targetId = null, string? targetDisplayName = null);

    void UpdateResolvedTarget(string viewerSessionId, CaptureTargetDescriptor target);

    Task CloseViewerApplicationAsync(string viewerSessionId, string reason, CancellationToken cancellationToken = default);

    void UntrackViewerSession(string viewerSessionId);
}
