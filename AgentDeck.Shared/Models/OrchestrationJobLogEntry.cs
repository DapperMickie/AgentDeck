using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Single log entry emitted by an orchestration job.</summary>
public sealed class OrchestrationJobLogEntry
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public OrchestrationLogLevel Level { get; init; } = OrchestrationLogLevel.Information;
    public string Message { get; init; } = string.Empty;
    public string? MachineId { get; init; }
}
