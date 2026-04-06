namespace AgentDeck.Core.Models;

/// <summary>Result of a Docker operation triggered for a workload.</summary>
public sealed class WorkloadContainerExecutionResult
{
    public required bool Succeeded { get; init; }
    public required int ExitCode { get; init; }
    public required string CommandText { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
}
