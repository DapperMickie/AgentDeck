using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Request payload for moving an orchestration job through its lifecycle.</summary>
public sealed class UpdateOrchestrationJobStatusRequest
{
    public OrchestrationJobStatus Status { get; init; }
    public string? Message { get; init; }
    public string? SessionId { get; init; }
    public string? ViewerSessionId { get; init; }
    public int? ExitCode { get; init; }
}
