namespace AgentDeck.Shared.Models;

public sealed class RegisteredCompanion
{
    public string CompanionId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Platform { get; init; }
    public string? AppVersion { get; init; }
    public string? ConnectionId { get; init; }
    public DateTimeOffset RegisteredAt { get; init; }
    public DateTimeOffset LastSeenAt { get; init; }
    public bool IsConnected { get; init; }
    public IReadOnlyList<string> AttachedMachineIds { get; init; } = [];
    public IReadOnlyList<string> AttachedSessionIds { get; init; } = [];
}
