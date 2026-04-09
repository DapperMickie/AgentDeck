using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

public sealed class UpdateProjectSessionControlRequest
{
    public ProjectSessionControlRequestMode Mode { get; init; } = ProjectSessionControlRequestMode.Request;
}
