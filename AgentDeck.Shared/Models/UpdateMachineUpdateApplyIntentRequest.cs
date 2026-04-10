using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Updates the coordinator-owned runner update apply intent for a specific machine.</summary>
public sealed class UpdateMachineUpdateApplyIntentRequest
{
    public MachineUpdateApplyIntentMode Mode { get; init; } = MachineUpdateApplyIntentMode.Inherit;
}
