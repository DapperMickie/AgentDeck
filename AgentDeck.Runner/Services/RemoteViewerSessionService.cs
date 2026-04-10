using AgentDeck.Runner.Configuration;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;
using Microsoft.Extensions.Options;

namespace AgentDeck.Runner.Services;

/// <inheritdoc />
public sealed class RemoteViewerSessionService : IRemoteViewerSessionService
{
    private const int MaxRetainedTerminalHistory = 100;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RemoteViewerSession> _sessions = [];
    private readonly DesktopViewerTransportOptions _desktopTransportOptions;

    public RemoteViewerSessionService(IOptions<DesktopViewerTransportOptions> desktopTransportOptions)
    {
        _desktopTransportOptions = desktopTransportOptions.Value;
    }

    public IReadOnlyList<RemoteViewerProviderCapability> GetAvailableProviders()
    {
        var managedProvider = GetManagedProviderCapability();
        if (OperatingSystem.IsWindows())
        {
            var providers = new List<RemoteViewerProviderCapability>();
            if (managedProvider is not null)
            {
                providers.Add(managedProvider);
            }

            providers.AddRange(
            [
                new RemoteViewerProviderCapability
                {
                    Provider = RemoteViewerProviderKind.Rdp,
                    DisplayName = "RDP-compatible desktop viewer",
                    SupportedTargets = [RemoteViewerTargetKind.Desktop],
                    RequiresInteractiveDesktop = true,
                    Notes = "Best fit for full Windows desktop access."
                },
                new RemoteViewerProviderCapability
                {
                    Provider = RemoteViewerProviderKind.Vnc,
                    DisplayName = "VNC-compatible viewer",
                    SupportedTargets = [RemoteViewerTargetKind.Desktop, RemoteViewerTargetKind.Window, RemoteViewerTargetKind.Emulator, RemoteViewerTargetKind.VsCode],
                    RequiresInteractiveDesktop = true,
                    Notes = "Window targeting still requires platform-specific capture support."
                }
            ]);

            return providers;
        }

        if (OperatingSystem.IsMacOS())
        {
            var providers = new List<RemoteViewerProviderCapability>();
            if (managedProvider is not null)
            {
                providers.Add(managedProvider);
            }

            providers.AddRange(
            [
                new RemoteViewerProviderCapability
                {
                    Provider = RemoteViewerProviderKind.ScreenSharing,
                    DisplayName = "Screen Sharing",
                    SupportedTargets = [RemoteViewerTargetKind.Desktop, RemoteViewerTargetKind.Window, RemoteViewerTargetKind.Simulator, RemoteViewerTargetKind.VsCode],
                    RequiresInteractiveDesktop = true,
                    Notes = "macOS screen sharing can back desktop and simulator viewing."
                },
                new RemoteViewerProviderCapability
                {
                    Provider = RemoteViewerProviderKind.Vnc,
                    DisplayName = "VNC-compatible viewer",
                    SupportedTargets = [RemoteViewerTargetKind.Desktop, RemoteViewerTargetKind.Window],
                    RequiresInteractiveDesktop = true,
                    Notes = "Window-level targeting is still an orchestration concern above the transport."
                }
            ]);

            return providers;
        }

        if (OperatingSystem.IsLinux())
        {
            var providers = new List<RemoteViewerProviderCapability>();
            if (managedProvider is not null)
            {
                providers.Add(managedProvider);
            }

            providers.AddRange(
            [
                new RemoteViewerProviderCapability
                {
                    Provider = RemoteViewerProviderKind.Vnc,
                    DisplayName = "VNC-compatible viewer",
                    SupportedTargets = [RemoteViewerTargetKind.Desktop, RemoteViewerTargetKind.Window, RemoteViewerTargetKind.Emulator, RemoteViewerTargetKind.VsCode],
                    RequiresInteractiveDesktop = true,
                    Notes = "Good general-purpose transport for Linux desktop and app surfaces."
                },
                new RemoteViewerProviderCapability
                {
                    Provider = RemoteViewerProviderKind.X11,
                    DisplayName = "X11 window stream",
                    SupportedTargets = [RemoteViewerTargetKind.Window],
                    RequiresInteractiveDesktop = true,
                    Notes = "Best fit when window-level capture is available on X11."
                },
                new RemoteViewerProviderCapability
                {
                    Provider = RemoteViewerProviderKind.Wayland,
                    DisplayName = "Wayland capture session",
                    SupportedTargets = [RemoteViewerTargetKind.Desktop, RemoteViewerTargetKind.Window],
                    RequiresInteractiveDesktop = true,
                    Notes = "Wayland support depends on compositor-specific capture hooks."
                }
            ]);

            return providers;
        }

        return managedProvider is null ? [] : [managedProvider];
    }

    public RemoteViewerSession Create(CreateRemoteViewerSessionRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var resolvedProvider = ResolveProvider(request.Provider, request.Target.Kind);
        var session = new RemoteViewerSession
        {
            Id = Guid.NewGuid().ToString("N"),
            MachineId = request.MachineId,
            MachineName = request.MachineName,
            JobId = request.JobId ?? request.Target.JobId,
            Target = CloneTarget(request.Target),
            Provider = resolvedProvider,
            Status = RemoteViewerSessionStatus.Requested,
            StatusMessage = "Viewer session requested.",
            CreatedAt = now,
            UpdatedAt = now
        };

        lock (_gate)
        {
            _sessions[session.Id] = session;
            PruneTerminalSessions(session.Id);
        }

        return CloneSession(session);
    }

    public RemoteViewerSession? Get(string sessionId)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(sessionId, out var session) ? CloneSession(session) : null;
        }
    }

    public IReadOnlyList<RemoteViewerSession> GetAll()
    {
        lock (_gate)
        {
            return [.. _sessions.Values.OrderByDescending(session => session.CreatedAt).Select(CloneSession)];
        }
    }

    public RemoteViewerSessionMutationResult Update(string sessionId, UpdateRemoteViewerSessionRequest request)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                return new RemoteViewerSessionMutationResult
                {
                    Outcome = RemoteViewerSessionMutationOutcome.NotFound
                };
            }

            if (!IsValidTransition(session.Status, request.Status))
            {
                return new RemoteViewerSessionMutationResult
                {
                    Outcome = RemoteViewerSessionMutationOutcome.InvalidTransition,
                    Session = CloneSession(session)
                };
            }

            if (session.Status == RemoteViewerSessionStatus.Closed && request.Status == RemoteViewerSessionStatus.Closed)
            {
                var updatedClosedSession = new RemoteViewerSession(session)
                {
                    StatusMessage = request.Message ?? session.StatusMessage,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                _sessions[sessionId] = updatedClosedSession;
                PruneTerminalSessions(sessionId);

                return new RemoteViewerSessionMutationResult
                {
                    Outcome = RemoteViewerSessionMutationOutcome.Updated,
                    Session = CloneSession(updatedClosedSession)
                };
            }

            var updated = new RemoteViewerSession(session)
            {
                Status = request.Status,
                ConnectionUri = request.ConnectionUri ?? session.ConnectionUri,
                AccessToken = request.AccessToken ?? session.AccessToken,
                StatusMessage = request.Message ?? session.StatusMessage,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _sessions[sessionId] = updated;
            PruneTerminalSessions(sessionId);
            return new RemoteViewerSessionMutationResult
            {
                Outcome = RemoteViewerSessionMutationOutcome.Updated,
                Session = CloneSession(updated)
            };
        }
    }

    public RemoteViewerSessionMutationResult Close(string sessionId, string? message = null)
    {
        return Update(sessionId, new UpdateRemoteViewerSessionRequest
        {
            Status = RemoteViewerSessionStatus.Closed,
            Message = message ?? "Viewer session closed."
        });
    }

    private static bool IsValidTransition(RemoteViewerSessionStatus from, RemoteViewerSessionStatus to)
    {
        if (from == to)
        {
            return true;
        }

        return (from, to) switch
        {
            (RemoteViewerSessionStatus.Requested, RemoteViewerSessionStatus.Preparing) => true,
            (RemoteViewerSessionStatus.Requested, RemoteViewerSessionStatus.Failed) => true,
            (RemoteViewerSessionStatus.Requested, RemoteViewerSessionStatus.Closed) => true,
            (RemoteViewerSessionStatus.Preparing, RemoteViewerSessionStatus.Ready) => true,
            (RemoteViewerSessionStatus.Preparing, RemoteViewerSessionStatus.Failed) => true,
            (RemoteViewerSessionStatus.Preparing, RemoteViewerSessionStatus.Closed) => true,
            (RemoteViewerSessionStatus.Ready, RemoteViewerSessionStatus.Closed) => true,
            (RemoteViewerSessionStatus.Ready, RemoteViewerSessionStatus.Failed) => true,
            (RemoteViewerSessionStatus.Failed, RemoteViewerSessionStatus.Closed) => true,
            _ => false
        };
    }

    private static RemoteViewerSession CloneSession(RemoteViewerSession session)
    {
        return new RemoteViewerSession(session);
    }

    private static RemoteViewerTarget CloneTarget(RemoteViewerTarget target)
    {
        return new RemoteViewerTarget
        {
            Kind = target.Kind,
            DisplayName = target.DisplayName,
            JobId = target.JobId,
            SessionId = target.SessionId,
            WindowTitle = target.WindowTitle,
            VirtualDeviceId = target.VirtualDeviceId,
            VirtualDeviceProfileId = target.VirtualDeviceProfileId
        };
    }

    private RemoteViewerProviderKind ResolveProvider(RemoteViewerProviderKind requestedProvider, RemoteViewerTargetKind targetKind)
    {
        if (requestedProvider != RemoteViewerProviderKind.Auto)
        {
            return requestedProvider;
        }

        var automaticProvider = GetAvailableProviders()
            .FirstOrDefault(capability => capability.SupportedTargets.Contains(targetKind));

        return automaticProvider?.Provider ?? RemoteViewerProviderKind.Auto;
    }

    private RemoteViewerProviderCapability? GetManagedProviderCapability()
    {
        if (!_desktopTransportOptions.Managed.IsConfigured)
        {
            return null;
        }

        return new RemoteViewerProviderCapability
        {
            Provider = RemoteViewerProviderKind.Managed,
            DisplayName = "AgentDeck-managed viewer transport",
            SupportedTargets =
            [
                RemoteViewerTargetKind.Desktop,
                RemoteViewerTargetKind.Window,
                RemoteViewerTargetKind.Emulator,
                RemoteViewerTargetKind.Simulator,
                RemoteViewerTargetKind.VsCode
            ],
            RequiresInteractiveDesktop = true,
            Notes = "Runner launches a configured AgentDeck-managed helper and advertises its connection URI for supported viewer targets."
        };
    }

    private void PruneTerminalSessions(string protectedSessionId)
    {
        var orderedTerminalSessions = _sessions.Values
            .Where(session => session.Status is RemoteViewerSessionStatus.Closed or RemoteViewerSessionStatus.Failed)
            .OrderByDescending(session => session.UpdatedAt)
            .ThenByDescending(session => session.CreatedAt)
            .ThenBy(session => session.Id, StringComparer.Ordinal)
            .ToList();

        var retainedIds = orderedTerminalSessions
            .Take(MaxRetainedTerminalHistory)
            .Select(session => session.Id)
            .ToHashSet(StringComparer.Ordinal);

        retainedIds.Add(protectedSessionId);

        var removableIds = orderedTerminalSessions
            .Where(session => !retainedIds.Contains(session.Id))
            .Select(session => session.Id)
            .ToList();

        foreach (var sessionId in removableIds)
        {
            _sessions.Remove(sessionId);
        }
    }
}
