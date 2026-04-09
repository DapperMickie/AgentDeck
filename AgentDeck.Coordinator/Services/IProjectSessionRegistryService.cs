using AgentDeck.Shared.Models;

namespace AgentDeck.Coordinator.Services;

public interface IProjectSessionRegistryService
{
    IReadOnlyList<ProjectSessionRecord> GetSessions(string? projectId = null);
    ProjectSessionRecord? GetSession(string projectSessionId);
    ProjectSessionRecord CreateSession(string projectId, string projectName, string? machineId, string? machineName, string? companionId);
    ProjectSessionRecord RegisterSurface(string projectSessionId, RegisterProjectSessionSurfaceRequest request);
}
