using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

/// <summary>Installs supported CLIs and SDKs on the runner machine.</summary>
public interface IMachineSetupService
{
    Task<MachineCapabilityInstallResult> InstallCapabilityAsync(string capabilityId, string? version = null, CancellationToken cancellationToken = default);
}
