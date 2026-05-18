namespace AgentDeck.Shared.Models;

public sealed class RunnerOrchestratorInstanceEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string Message { get; init; } = string.Empty;
    public string Level { get; init; } = "info";
}
