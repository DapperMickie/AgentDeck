namespace AgentDeck.Shared.Models;

public sealed class RegisterCompanionRequest
{
    public string? DisplayName { get; init; }
    public string? Platform { get; init; }
    public string? AppVersion { get; init; }
}
