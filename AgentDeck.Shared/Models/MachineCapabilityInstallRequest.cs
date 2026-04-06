namespace AgentDeck.Shared.Models;

/// <summary>Optional parameters for installing a supported capability on a runner machine.</summary>
public sealed class MachineCapabilityInstallRequest
{
    public string? Version { get; init; }
}
