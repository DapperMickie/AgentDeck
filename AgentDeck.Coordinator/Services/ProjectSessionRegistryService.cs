using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

namespace AgentDeck.Coordinator.Services;

public sealed class ProjectSessionRegistryService : IProjectSessionRegistryService
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ProjectSessionRecord> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ProjectSessionRegistryService> _logger;

    public ProjectSessionRegistryService(ILogger<ProjectSessionRegistryService> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<ProjectSessionRecord> GetSessions(string? projectId = null)
    {
        lock (_lock)
        {
            return _sessions.Values
                .Where(session => string.IsNullOrWhiteSpace(projectId) || string.Equals(session.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(session => session.UpdatedAt)
                .Select(CloneSession)
                .ToArray();
        }
    }

    public ProjectSessionRecord? GetSession(string projectSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectSessionId);

        lock (_lock)
        {
            return _sessions.TryGetValue(projectSessionId, out var session)
                ? CloneSession(session)
                : null;
        }
    }

    public ProjectSessionRecord? GetSessionBySurfaceReference(string referenceId, ProjectSessionSurfaceKind? kind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceId);

        lock (_lock)
        {
            var normalizedReferenceId = referenceId.Trim();
            var session = _sessions.Values.FirstOrDefault(session =>
                session.Surfaces.Any(surface =>
                    string.Equals(surface.ReferenceId, normalizedReferenceId, StringComparison.OrdinalIgnoreCase) &&
                    (kind is null || surface.Kind == kind)));
            return session is null ? null : CloneSession(session);
        }
    }

    public ProjectSessionRecord CreateSession(string projectId, string projectName, string? machineId, string? machineName, string? companionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);

        var now = DateTimeOffset.UtcNow;
        var normalizedCompanionId = Normalize(companionId);
        var session = new ProjectSessionRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            ProjectId = projectId.Trim(),
            ProjectName = projectName.Trim(),
            MachineId = Normalize(machineId),
            MachineName = Normalize(machineName),
            CompanionId = normalizedCompanionId,
            ControlUpdatedAt = now,
            AttachedCompanionIds = string.IsNullOrWhiteSpace(normalizedCompanionId) ? [] : [normalizedCompanionId],
            CreatedAt = now,
            UpdatedAt = now
        };

        lock (_lock)
        {
            _sessions[session.Id] = session;
        }

        _logger.LogInformation(
            "Created project session {ProjectSessionId} for {ProjectId} on machine {MachineId}",
            session.Id,
            session.ProjectId,
            session.MachineId ?? "<unknown>");

        return CloneSession(session);
    }

    public bool RemoveSession(string projectSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectSessionId);

        lock (_lock)
        {
            return _sessions.Remove(projectSessionId.Trim());
        }
    }

    public void DetachCompanionFromAll(string companionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(companionId);

        var normalizedCompanionId = companionId.Trim();
        lock (_lock)
        {
            foreach (var session in _sessions.Values.ToArray())
            {
                if (!session.AttachedCompanionIds.Contains(normalizedCompanionId, StringComparer.OrdinalIgnoreCase) &&
                    !session.ViewerCompanionIds.Contains(normalizedCompanionId, StringComparer.OrdinalIgnoreCase) &&
                    !string.Equals(session.CompanionId, normalizedCompanionId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _sessions[session.Id] = RemoveCompanion(session, normalizedCompanionId, DateTimeOffset.UtcNow);
            }
        }
    }

    public ProjectSessionRecord AttachCompanion(string projectSessionId, string companionId, bool viewerOnly = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(companionId);

        lock (_lock)
        {
            var existingSession = RequireSession(projectSessionId);
            var normalizedCompanionId = companionId.Trim();
            var attachedCompanions = existingSession.AttachedCompanionIds
                .Append(normalizedCompanionId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var viewerCompanions = existingSession.ViewerCompanionIds
                .Where(id => !string.Equals(id, normalizedCompanionId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var controllerCompanionId = existingSession.CompanionId;
            if (viewerOnly || !string.IsNullOrWhiteSpace(controllerCompanionId) && !string.Equals(controllerCompanionId, normalizedCompanionId, StringComparison.OrdinalIgnoreCase))
            {
                viewerCompanions.Add(normalizedCompanionId);
            }
            else
            {
                controllerCompanionId = normalizedCompanionId;
            }

            var now = DateTimeOffset.UtcNow;
            var updatedSession = new ProjectSessionRecord
            {
                Id = existingSession.Id,
                ProjectId = existingSession.ProjectId,
                ProjectName = existingSession.ProjectName,
                MachineId = existingSession.MachineId,
                MachineName = existingSession.MachineName,
                CompanionId = controllerCompanionId,
                ControlUpdatedAt = string.Equals(existingSession.CompanionId, controllerCompanionId, StringComparison.OrdinalIgnoreCase)
                    ? existingSession.ControlUpdatedAt
                    : now,
                AttachedCompanionIds = attachedCompanions,
                ViewerCompanionIds = viewerCompanions
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                Status = existingSession.Status,
                CreatedAt = existingSession.CreatedAt,
                UpdatedAt = now,
                Surfaces = existingSession.Surfaces.Select(CloneSurface).ToArray()
            };

            _sessions[existingSession.Id] = updatedSession;
            return CloneSession(updatedSession);
        }
    }

    public ProjectSessionRecord DetachCompanion(string projectSessionId, string companionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(companionId);

        lock (_lock)
        {
            var existingSession = RequireSession(projectSessionId);
            var updatedSession = RemoveCompanion(existingSession, companionId.Trim(), DateTimeOffset.UtcNow);
            _sessions[existingSession.Id] = updatedSession;
            return CloneSession(updatedSession);
        }
    }

    public ProjectSessionRecord UpdateControl(string projectSessionId, string companionId, ProjectSessionControlRequestMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(companionId);

        lock (_lock)
        {
            var existingSession = RequireSession(projectSessionId);
            var normalizedCompanionId = companionId.Trim();
            var now = DateTimeOffset.UtcNow;
            var attachedCompanions = existingSession.AttachedCompanionIds
                .Append(normalizedCompanionId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var viewerCompanions = existingSession.ViewerCompanionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var controllerCompanionId = existingSession.CompanionId;

            switch (mode)
            {
                case ProjectSessionControlRequestMode.Request:
                    if (!string.IsNullOrWhiteSpace(controllerCompanionId) &&
                        !string.Equals(controllerCompanionId, normalizedCompanionId, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Project session '{existingSession.Id}' is currently controlled by companion '{controllerCompanionId}'.");
                    }

                    controllerCompanionId = normalizedCompanionId;
                    viewerCompanions.Remove(normalizedCompanionId);
                    break;

                case ProjectSessionControlRequestMode.ForceTakeover:
                    if (!string.IsNullOrWhiteSpace(controllerCompanionId) &&
                        !string.Equals(controllerCompanionId, normalizedCompanionId, StringComparison.OrdinalIgnoreCase))
                    {
                        attachedCompanions.Add(controllerCompanionId);
                        viewerCompanions.Add(controllerCompanionId);
                    }

                    controllerCompanionId = normalizedCompanionId;
                    viewerCompanions.Remove(normalizedCompanionId);
                    break;

                case ProjectSessionControlRequestMode.Yield:
                    if (!string.Equals(controllerCompanionId, normalizedCompanionId, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Companion '{normalizedCompanionId}' does not currently control project session '{existingSession.Id}'.");
                    }

                    controllerCompanionId = null;
                    viewerCompanions.Add(normalizedCompanionId);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported project session control mode.");
            }

            var updatedSession = new ProjectSessionRecord
            {
                Id = existingSession.Id,
                ProjectId = existingSession.ProjectId,
                ProjectName = existingSession.ProjectName,
                MachineId = existingSession.MachineId,
                MachineName = existingSession.MachineName,
                CompanionId = controllerCompanionId,
                ControlUpdatedAt = now,
                AttachedCompanionIds = attachedCompanions.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
                ViewerCompanionIds = viewerCompanions.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
                Status = existingSession.Status,
                CreatedAt = existingSession.CreatedAt,
                UpdatedAt = now,
                Surfaces = existingSession.Surfaces.Select(CloneSurface).ToArray()
            };

            _sessions[existingSession.Id] = updatedSession;
            return CloneSession(updatedSession);
        }
    }

    public bool CanCompanionControlSurface(string referenceId, string companionId, ProjectSessionSurfaceKind? kind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(companionId);

        var session = GetSessionBySurfaceReference(referenceId, kind);
        return session is null ||
            string.IsNullOrWhiteSpace(session.CompanionId) ||
            string.Equals(session.CompanionId, companionId.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public ProjectSessionRecord RegisterSurface(string projectSessionId, RegisterProjectSessionSurfaceRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectSessionId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DisplayName);

        lock (_lock)
        {
            if (!_sessions.TryGetValue(projectSessionId, out var existingSession))
            {
                throw new InvalidOperationException($"Coordinator does not know project session '{projectSessionId}'.");
            }

            var normalizedSurfaceId = Normalize(request.SurfaceId);
            var normalizedReferenceId = Normalize(request.ReferenceId);
            var existingSurface = existingSession.Surfaces.FirstOrDefault(surface =>
                (!string.IsNullOrWhiteSpace(normalizedSurfaceId) && string.Equals(surface.Id, normalizedSurfaceId, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(normalizedReferenceId) &&
                 surface.Kind == request.Kind &&
                 string.Equals(surface.ReferenceId, normalizedReferenceId, StringComparison.OrdinalIgnoreCase)));

            var now = DateTimeOffset.UtcNow;
            var surface = new ProjectSessionSurface
            {
                Id = existingSurface?.Id ?? normalizedSurfaceId ?? Guid.NewGuid().ToString("N"),
                ProjectSessionId = existingSession.Id,
                Kind = request.Kind,
                DisplayName = request.DisplayName.Trim(),
                MachineId = Normalize(request.MachineId) ?? existingSession.MachineId,
                MachineName = Normalize(request.MachineName) ?? existingSession.MachineName,
                ReferenceId = normalizedReferenceId,
                JobId = Normalize(request.JobId),
                Platform = request.Platform,
                LaunchMode = request.LaunchMode,
                ViewerTargetKind = request.ViewerTargetKind,
                Status = request.Status,
                StatusMessage = Normalize(request.StatusMessage),
                CreatedAt = existingSurface?.CreatedAt ?? now,
                UpdatedAt = now
            };

            var surfaces = existingSession.Surfaces
                .Where(existing => !string.Equals(existing.Id, surface.Id, StringComparison.OrdinalIgnoreCase))
                .Append(surface)
                .OrderBy(existing => existing.CreatedAt)
                .ToArray();

            var updatedSession = new ProjectSessionRecord
            {
                Id = existingSession.Id,
                ProjectId = existingSession.ProjectId,
                ProjectName = existingSession.ProjectName,
                MachineId = existingSession.MachineId,
                MachineName = existingSession.MachineName,
                CompanionId = existingSession.CompanionId,
                ControlUpdatedAt = existingSession.ControlUpdatedAt,
                AttachedCompanionIds = existingSession.AttachedCompanionIds.ToArray(),
                ViewerCompanionIds = existingSession.ViewerCompanionIds.ToArray(),
                Status = existingSession.Status,
                CreatedAt = existingSession.CreatedAt,
                UpdatedAt = now,
                Surfaces = surfaces
            };

            _sessions[existingSession.Id] = updatedSession;
            _logger.LogInformation(
                "Registered project session surface {SurfaceId} ({Kind}) for session {ProjectSessionId}",
                surface.Id,
                surface.Kind,
                existingSession.Id);

            return CloneSession(updatedSession);
        }
    }

    private static ProjectSessionRecord CloneSession(ProjectSessionRecord session)
    {
        return new ProjectSessionRecord
        {
            Id = session.Id,
            ProjectId = session.ProjectId,
            ProjectName = session.ProjectName,
            MachineId = session.MachineId,
            MachineName = session.MachineName,
            CompanionId = session.CompanionId,
            ControlUpdatedAt = session.ControlUpdatedAt,
            AttachedCompanionIds = session.AttachedCompanionIds.ToArray(),
            ViewerCompanionIds = session.ViewerCompanionIds.ToArray(),
            Status = session.Status,
            CreatedAt = session.CreatedAt,
            UpdatedAt = session.UpdatedAt,
            Surfaces = session.Surfaces.Select(CloneSurface).ToArray()
        };
    }

    private static ProjectSessionSurface CloneSurface(ProjectSessionSurface surface)
    {
        return new ProjectSessionSurface
        {
            Id = surface.Id,
            ProjectSessionId = surface.ProjectSessionId,
            Kind = surface.Kind,
            DisplayName = surface.DisplayName,
            MachineId = surface.MachineId,
            MachineName = surface.MachineName,
            ReferenceId = surface.ReferenceId,
            JobId = surface.JobId,
            Platform = surface.Platform,
            LaunchMode = surface.LaunchMode,
            ViewerTargetKind = surface.ViewerTargetKind,
            Status = surface.Status,
            StatusMessage = surface.StatusMessage,
            CreatedAt = surface.CreatedAt,
            UpdatedAt = surface.UpdatedAt
        };
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private ProjectSessionRecord RequireSession(string projectSessionId)
    {
        if (_sessions.TryGetValue(projectSessionId.Trim(), out var session))
        {
            return session;
        }

        throw new InvalidOperationException($"Coordinator does not know project session '{projectSessionId}'.");
    }

    private static ProjectSessionRecord RemoveCompanion(ProjectSessionRecord session, string companionId, DateTimeOffset now)
    {
        var attachedCompanions = session.AttachedCompanionIds
            .Where(id => !string.Equals(id, companionId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var viewerCompanions = session.ViewerCompanionIds
            .Where(id => !string.Equals(id, companionId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var controllerCompanionId = string.Equals(session.CompanionId, companionId, StringComparison.OrdinalIgnoreCase)
            ? null
            : session.CompanionId;
        var controlUpdatedAt = string.Equals(session.CompanionId, controllerCompanionId, StringComparison.OrdinalIgnoreCase)
            ? session.ControlUpdatedAt
            : now;

        return new ProjectSessionRecord
        {
            Id = session.Id,
            ProjectId = session.ProjectId,
            ProjectName = session.ProjectName,
            MachineId = session.MachineId,
            MachineName = session.MachineName,
            CompanionId = controllerCompanionId,
            ControlUpdatedAt = controlUpdatedAt,
            AttachedCompanionIds = attachedCompanions,
            ViewerCompanionIds = viewerCompanions,
            Status = session.Status,
            CreatedAt = session.CreatedAt,
            UpdatedAt = now,
            Surfaces = session.Surfaces.Select(CloneSurface).ToArray()
        };
    }
}
