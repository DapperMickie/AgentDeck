using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Request payload for appending a log entry to an orchestration job.</summary>
public sealed class AppendOrchestrationJobLogRequest
{
    public OrchestrationLogLevel Level { get; init; } = OrchestrationLogLevel.Information;
    public string Message { get; init; } = string.Empty;
    public string? MachineId { get; init; }
}
