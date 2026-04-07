namespace AgentDeck.Runner.Services;

/// <summary>Result of evaluating a privileged runner action against the current trust policy.</summary>
public sealed class RunnerTrustDecision
{
    public bool Allowed { get; init; }
    public string Action { get; init; } = string.Empty;
    public string ActorId { get; init; } = string.Empty;
    public string ActorDisplayName { get; init; } = string.Empty;
    public string? RemoteAddress { get; init; }
    public string? UserAgent { get; init; }
    public bool IsLoopback { get; init; }
    public string TargetType { get; init; } = string.Empty;
    public string? TargetId { get; init; }
    public string? TargetDisplayName { get; init; }
    public string? Message { get; init; }
}
