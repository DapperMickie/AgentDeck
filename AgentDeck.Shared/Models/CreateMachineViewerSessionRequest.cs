namespace AgentDeck.Shared.Models;

/// <summary>Coordinator-brokered request to create a viewer session on a machine.</summary>
public sealed class CreateMachineViewerSessionRequest
{
    public CreateRemoteViewerSessionRequest Viewer { get; init; } = new();
    public bool ForceTakeover { get; init; }
}
