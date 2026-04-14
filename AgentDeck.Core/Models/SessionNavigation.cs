using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

namespace AgentDeck.Core.Models;

public static class SessionNavigation
{
    public static IReadOnlyList<TerminalSession> GetStandaloneTerminalSessions(
        IEnumerable<TerminalSession> sessions,
        IEnumerable<ProjectSessionRecord> projectSessions)
    {
        var projectTerminalIds = GetProjectTerminalIds(projectSessions);
        return sessions
            .Where(session => !projectTerminalIds.Contains(session.Id))
            .ToArray();
    }

    public static ProjectSessionRecord? GetProjectSessionForTerminal(
        string terminalSessionId,
        IEnumerable<ProjectSessionRecord> projectSessions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalSessionId);

        var normalizedTerminalSessionId = terminalSessionId.Trim();
        return projectSessions
            .OrderByDescending(session => session.UpdatedAt)
            .FirstOrDefault(session => session.Surfaces.Any(surface =>
                surface.Kind == ProjectSessionSurfaceKind.Terminal &&
                string.Equals(surface.ReferenceId, normalizedTerminalSessionId, StringComparison.OrdinalIgnoreCase)));
    }

    private static HashSet<string> GetProjectTerminalIds(IEnumerable<ProjectSessionRecord> projectSessions) =>
        projectSessions
            .SelectMany(session => session.Surfaces)
            .Where(surface =>
                surface.Kind == ProjectSessionSurfaceKind.Terminal &&
                !string.IsNullOrWhiteSpace(surface.ReferenceId))
            .Select(surface => surface.ReferenceId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
