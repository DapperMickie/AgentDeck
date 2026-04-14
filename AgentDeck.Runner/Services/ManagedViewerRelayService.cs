using System.Collections.Concurrent;
using System.Security.Cryptography;
using AgentDeck.Runner.Configuration;
using AgentDeck.Runner.Hubs;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using RdpPoc.Contracts;
using RdpPoc.HostAgent.Sdk;

namespace AgentDeck.Runner.Services;

public sealed class ManagedViewerRelayService : IManagedViewerRelayService, IDisposable
{
    private sealed class ActiveRelaySession
    {
        public required RemoteViewerSession Session { get; init; }
        public required HostSessionAssignment Assignment { get; init; }
        public required string AccessToken { get; init; }
        public required CancellationTokenSource Cancellation { get; init; }
        public required Task CaptureLoop { get; set; }
        public required TaskCompletionSource<bool> FirstFrameReady { get; init; }
        public object InputSync { get; } = new();
        public ModifierKeyState ModifierState { get; set; }
        public RelayFrame? LatestFrame { get; set; }
    }

    private readonly ConcurrentDictionary<string, ActiveRelaySession> _activeSessions = new(StringComparer.Ordinal);
    private readonly IRemoteViewerSessionService _viewers;
    private readonly IHubContext<ManagedViewerRelayHub> _hubContext;
    private readonly ICoordinatorRunnerPublisher _coordinatorPublisher;
    private readonly ManagedDesktopViewerTransportOptions _options;
    private readonly ILogger<ManagedViewerRelayService> _logger;
    private readonly IHostCapturePlatform _capturePlatform;
    private readonly object _captureSync = new();

    public ManagedViewerRelayService(
        IRemoteViewerSessionService viewers,
        IHubContext<ManagedViewerRelayHub> hubContext,
        ICoordinatorRunnerPublisher coordinatorPublisher,
        IOptions<DesktopViewerTransportOptions> transportOptions,
        ILogger<ManagedViewerRelayService> logger)
    {
        _viewers = viewers;
        _hubContext = hubContext;
        _coordinatorPublisher = coordinatorPublisher;
        _options = transportOptions.Value.Managed;
        _logger = logger;
        _capturePlatform = HostCapturePlatformFactory.Create();
    }

    public async Task<ManagedViewerRelayBootstrapResult> StartAsync(
        RemoteViewerSession session,
        string connectionBaseUri,
        CancellationToken cancellationToken = default)
    {
        if (_activeSessions.TryGetValue(session.Id, out var existing))
        {
            return new ManagedViewerRelayBootstrapResult(
                BuildConnectionUri(connectionBaseUri),
                existing.AccessToken,
                $"{session.Target.DisplayName} is ready via AgentDeck-managed relay transport.");
        }

        var target = await ResolveTargetAsync(session, cancellationToken);
        var accessToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(Math.Max(1, _options.AccessTokenBytes)))
            .ToLowerInvariant();
        var assignment = new HostSessionAssignment(
            session.Id,
            target.Kind,
            target.Id,
            target.DisplayName,
            session.Target.DisplayName,
            accessToken);
        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var firstFrameReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeSession = new ActiveRelaySession
        {
            Session = session,
            Assignment = assignment,
            AccessToken = accessToken,
            Cancellation = linkedCancellation,
            FirstFrameReady = firstFrameReady,
            CaptureLoop = Task.CompletedTask
        };

        if (!_activeSessions.TryAdd(session.Id, activeSession))
        {
            linkedCancellation.Cancel();
            linkedCancellation.Dispose();
            throw new InvalidOperationException($"Viewer session '{session.Id}' is already active.");
        }

        activeSession.CaptureLoop = RunCaptureLoopAsync(activeSession);

        try
        {
            await activeSession.FirstFrameReady.Task.WaitAsync(_options.StartupTimeout, cancellationToken);
        }
        catch
        {
            await StopAsync(session.Id, cancellationToken);
            throw;
        }

        return new ManagedViewerRelayBootstrapResult(
            BuildConnectionUri(connectionBaseUri),
            accessToken,
            $"{target.DisplayName} is ready via AgentDeck-managed relay transport.");
    }

    public IReadOnlyList<CaptureTargetDescriptor> GetCaptureTargets()
    {
        lock (_captureSync)
        {
            return _capturePlatform.GetTargets();
        }
    }

    public async Task StopAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_activeSessions.TryRemove(sessionId, out var activeSession))
        {
            return;
        }

        activeSession.Cancellation.Cancel();
        try
        {
            await activeSession.CaptureLoop.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            activeSession.Cancellation.Dispose();
        }
    }

    public Task PublishSessionUpdatedAsync(RemoteViewerSession session, CancellationToken cancellationToken = default) =>
        PublishSessionUpdatedCoreAsync(session, cancellationToken);

    public RemoteViewerSession JoinViewer(string sessionId, string accessToken)
    {
        var activeSession = GetActiveSession(sessionId);
        ValidateAccessToken(activeSession, accessToken);
        return _viewers.Get(sessionId) ?? activeSession.Session;
    }

    public RelayFrame? GetLatestFrame(string sessionId) =>
        GetActiveSession(sessionId).LatestFrame;

    public async Task SendPointerInputAsync(
        string sessionId,
        string accessToken,
        PointerInputEvent input,
        CancellationToken cancellationToken = default)
    {
        var activeSession = GetActiveSession(sessionId);
        ValidateAccessToken(activeSession, accessToken);
        await SendPointerInputAsync(sessionId, input, cancellationToken);
    }

    public async Task SendKeyboardInputAsync(
        string sessionId,
        string accessToken,
        KeyboardInputEvent input,
        CancellationToken cancellationToken = default)
    {
        var activeSession = GetActiveSession(sessionId);
        ValidateAccessToken(activeSession, accessToken);
        await SendKeyboardInputAsync(sessionId, input, cancellationToken);
    }

    public async Task SendPointerInputAsync(
        string sessionId,
        PointerInputEvent input,
        CancellationToken cancellationToken = default)
    {
        var activeSession = GetActiveSession(sessionId);

        _logger.LogInformation(
            "Managed relay injecting pointer input for viewer {SessionId} target {TargetKind}:{TargetId} ({TargetDisplayName}): {EventType} x={X:F3} y={Y:F3} button={Button} clicks={ClickCount} wheel=({WheelDeltaX},{WheelDeltaY})",
            sessionId,
            activeSession.Assignment.TargetKind,
            activeSession.Assignment.TargetId,
            activeSession.Assignment.TargetDisplayName,
            input.EventType,
            input.X,
            input.Y,
            input.Button ?? "<none>",
            input.ClickCount,
            input.WheelDeltaX,
            input.WheelDeltaY);

        try
        {
            await Task.Run(() =>
            {
                lock (activeSession.InputSync)
                {
                    lock (_captureSync)
                    {
                        _capturePlatform.HandlePointerInput(activeSession.Assignment, input);
                    }
                }
            }, cancellationToken);
            _logger.LogInformation("Managed relay completed pointer input for viewer {SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Managed relay pointer input failed for viewer {SessionId}", sessionId);
            throw;
        }
    }

    public async Task SendKeyboardInputAsync(
        string sessionId,
        KeyboardInputEvent input,
        CancellationToken cancellationToken = default)
    {
        var activeSession = GetActiveSession(sessionId);

        _logger.LogInformation(
            "Managed relay injecting keyboard input for viewer {SessionId} target {TargetKind}:{TargetId} ({TargetDisplayName}): {EventType} {Code} alt={Alt} ctrl={Control} shift={Shift}",
            sessionId,
            activeSession.Assignment.TargetKind,
            activeSession.Assignment.TargetId,
            activeSession.Assignment.TargetDisplayName,
            input.EventType,
            input.Code,
            input.Alt,
            input.Control,
            input.Shift);

        try
        {
            await Task.Run(() =>
            {
                lock (activeSession.InputSync)
                {
                    lock (_captureSync)
                    {
                        activeSession.ModifierState = _capturePlatform.HandleKeyboardInput(
                            activeSession.Assignment,
                            input,
                            activeSession.ModifierState);
                    }
                }
            }, cancellationToken);
            _logger.LogInformation("Managed relay completed keyboard input for viewer {SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Managed relay keyboard input failed for viewer {SessionId}", sessionId);
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var sessionId in _activeSessions.Keys.ToArray())
        {
            try
            {
                StopAsync(sessionId).GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        _capturePlatform.Dispose();
    }

    private async Task RunCaptureLoopAsync(ActiveRelaySession activeSession)
    {
        try
        {
            long sequenceId = 0;
            while (!activeSession.Cancellation.IsCancellationRequested)
            {
                RelayFrame frame;
                lock (activeSession.InputSync)
                {
                    lock (_captureSync)
                    {
                        frame = _capturePlatform.CaptureFrame(activeSession.Assignment, sequenceId++);
                    }
                }

                activeSession.LatestFrame = frame;
                activeSession.FirstFrameReady.TrySetResult(true);
                await PublishFrameAsync(activeSession.Session.Id, frame, activeSession.Cancellation.Token);
                await Task.Delay(_options.FrameInterval, activeSession.Cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (activeSession.Cancellation.IsCancellationRequested)
        {
            activeSession.FirstFrameReady.TrySetCanceled(activeSession.Cancellation.Token);
        }
        catch (Exception ex)
        {
            activeSession.FirstFrameReady.TrySetException(ex);
            _logger.LogWarning(ex, "Managed relay capture failed for viewer session {SessionId}", activeSession.Session.Id);
            var failed = _viewers.Update(activeSession.Session.Id, new UpdateRemoteViewerSessionRequest
            {
                Status = RemoteViewerSessionStatus.Failed,
                Message = ex.Message
            }).Session;

            if (failed is not null)
            {
                await PublishSessionUpdatedAsync(failed);
            }

            _activeSessions.TryRemove(activeSession.Session.Id, out _);
            activeSession.Cancellation.Dispose();
        }
    }

    private async Task<CaptureTargetDescriptor> ResolveTargetAsync(
        RemoteViewerSession session,
        CancellationToken cancellationToken)
    {
        if (session.Target.Kind == RemoteViewerTargetKind.Desktop)
        {
            lock (_captureSync)
            {
                return _capturePlatform.GetTargets()
                    .FirstOrDefault(target => target.Kind == CaptureTargetKind.Desktop)
                    ?? throw new InvalidOperationException("The runner does not currently expose a desktop capture target.");
            }
        }

        var deadline = DateTimeOffset.UtcNow + _options.StartupTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = TryFindWindowTarget(session);
            if (match is not null)
            {
                return match;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                var requestedName = session.Target.WindowTitle ?? session.Target.DisplayName;
                throw new InvalidOperationException(
                    $"The runner could not resolve a window capture target for '{requestedName}'.");
            }

            await Task.Delay(250, cancellationToken);
        }
    }

    private CaptureTargetDescriptor? TryFindWindowTarget(RemoteViewerSession session)
    {
        CaptureTargetDescriptor[] candidates;
        lock (_captureSync)
        {
            candidates = _capturePlatform.GetTargets()
                .Where(target => target.Kind == CaptureTargetKind.Window)
                .ToArray();
        }
        if (candidates.Length == 0)
        {
            return null;
        }

        var baselineTargetIds = session.Target.KnownWindowTargetIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (baselineTargetIds.Count > 0)
        {
            var launchedCandidates = candidates
                .Where(candidate => !baselineTargetIds.Contains(candidate.Id))
                .ToArray();
            var launchedMatch = TryMatchWindowTarget(launchedCandidates, session.Target);
            if (launchedMatch is not null)
            {
                return launchedMatch;
            }

            if (launchedCandidates.Length == 1)
            {
                return launchedCandidates[0];
            }

            return null;
        }

        return TryMatchWindowTarget(candidates, session.Target);
    }

    private static CaptureTargetDescriptor? TryMatchWindowTarget(
        IReadOnlyList<CaptureTargetDescriptor> candidates,
        RemoteViewerTarget target)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var exactKeys = new[]
        {
            target.WindowTitle,
            target.DisplayName
        }
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

        foreach (var key in exactKeys)
        {
            var exact = candidates.FirstOrDefault(target => string.Equals(target.DisplayName, key, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        foreach (var key in exactKeys)
        {
            var contains = candidates.FirstOrDefault(target => target.DisplayName.Contains(key, StringComparison.OrdinalIgnoreCase));
            if (contains is not null)
            {
                return contains;
            }
        }

        return null;
    }

    private static void ValidateAccessToken(ActiveRelaySession session, string accessToken)
    {
        if (!string.Equals(session.AccessToken, accessToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The supplied viewer token is invalid.");
        }
    }

    private ActiveRelaySession GetActiveSession(string sessionId)
    {
        if (_activeSessions.TryGetValue(sessionId, out var session))
        {
            return session;
        }

        throw new KeyNotFoundException($"Viewer session '{sessionId}' is not active.");
    }

    private string BuildConnectionUri(string connectionBaseUri)
    {
        var baseUri = connectionBaseUri.TrimEnd('/');
        var hubPath = _options.RelayHubPath.StartsWith("/", StringComparison.Ordinal)
            ? _options.RelayHubPath
            : $"/{_options.RelayHubPath}";
        return $"{baseUri}{hubPath}";
    }

    private async Task PublishSessionUpdatedCoreAsync(RemoteViewerSession session, CancellationToken cancellationToken)
    {
        await _hubContext.Clients.Group(ManagedViewerRelayHub.GetViewerGroupName(session.Id))
            .SendAsync("SessionUpdated", session, cancellationToken);
        await _coordinatorPublisher.PublishViewerSessionUpdatedAsync(session, cancellationToken);
    }

    private async Task PublishFrameAsync(string sessionId, RelayFrame frame, CancellationToken cancellationToken)
    {
        await _hubContext.Clients.Group(ManagedViewerRelayHub.GetViewerGroupName(sessionId))
            .SendAsync("FramePublished", frame, cancellationToken);
        await _coordinatorPublisher.PublishViewerFrameAsync(
            new RemoteViewerRelayFrame(
                sessionId,
                frame.SequenceId,
                frame.CapturedAt,
                frame.ContentType,
                frame.Width,
                frame.Height,
                frame.Payload),
            cancellationToken);
    }
}
