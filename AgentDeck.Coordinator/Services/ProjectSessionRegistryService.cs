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

    public ProjectSessionRecord CreateSession(string projectId, string projectName, string? machineId, string? machineName, string? companionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);

        var now = DateTimeOffset.UtcNow;
        var session = new ProjectSessionRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            ProjectId = projectId.Trim(),
            ProjectName = projectName.Trim(),
            MachineId = Normalize(machineId),
            MachineName = Normalize(machineName),
            CompanionId = Normalize(companionId),
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
}
