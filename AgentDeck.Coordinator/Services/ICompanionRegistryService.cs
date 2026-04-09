using AgentDeck.Shared.Models;

namespace AgentDeck.Coordinator.Services;

public interface ICompanionRegistryService
{
    RegisteredCompanion RegisterCompanion(RegisterCompanionRequest request);
    IReadOnlyList<RegisteredCompanion> GetCompanions();
    RegisteredCompanion? GetCompanion(string companionId);
    RegisteredCompanion AttachConnection(string companionId, string connectionId);
    void DisconnectConnection(string connectionId);
    string? GetCompanionIdByConnection(string connectionId);
    void AttachMachine(string companionId, string machineId);
    void DetachMachine(string companionId, string machineId);
    void AttachSession(string companionId, string sessionId);
    void DetachSession(string companionId, string sessionId);
    void RemoveSessionFromAll(string sessionId);
}
