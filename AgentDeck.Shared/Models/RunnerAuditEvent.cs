using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Audit record for a privileged action handled by the runner.</summary>
public sealed class RunnerAuditEvent
{
    public required string Id { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string Action { get; init; } = string.Empty;
    public RunnerAuditOutcome Outcome { get; init; }
    public string ActorId { get; init; } = string.Empty;
    public string ActorDisplayName { get; init; } = string.Empty;
    public string? RemoteAddress { get; init; }
    public string? UserAgent { get; init; }
    public string TargetType { get; init; } = string.Empty;
    public string? TargetId { get; init; }
    public string? TargetDisplayName { get; init; }
    public string? Details { get; init; }
}
