using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

/// <summary>Detects supported CLIs and SDKs available on the runner machine.</summary>
public interface IMachineCapabilityService
{
    Task<MachineCapabilitiesSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}
