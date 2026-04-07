namespace AgentDeck.Runner.Services;

/// <summary>Starts and cancels real runner-side execution for orchestration jobs.</summary>
public interface IOrchestrationExecutionService
{
    bool Start(string jobId);
    Task RequestCancellationAsync(string jobId, CancellationToken cancellationToken = default);
}
