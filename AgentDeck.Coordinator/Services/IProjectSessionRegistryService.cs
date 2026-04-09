using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

namespace AgentDeck.Coordinator.Services;

public interface IProjectSessionRegistryService
{
    IReadOnlyList<ProjectSessionRecord> GetSessions(string? projectId = null);
    ProjectSessionRecord? GetSession(string projectSessionId);
    ProjectSessionRecord? GetSessionBySurfaceReference(string referenceId, ProjectSessionSurfaceKind? kind = null);
    ProjectSessionRecord CreateSession(string projectId, string projectName, string? machineId, string? machineName, string? companionId);
    bool RemoveSession(string projectSessionId);
    void DetachCompanionFromAll(string companionId);
    ProjectSessionRecord AttachCompanion(string projectSessionId, string companionId, bool viewerOnly = true);
    ProjectSessionRecord DetachCompanion(string projectSessionId, string companionId);
    ProjectSessionRecord UpdateControl(string projectSessionId, string companionId, ProjectSessionControlRequestMode mode);
    bool CanCompanionControlSurface(string referenceId, string companionId, ProjectSessionSurfaceKind? kind = null);
    ProjectSessionRecord RegisterSurface(string projectSessionId, RegisterProjectSessionSurfaceRequest request);
}
