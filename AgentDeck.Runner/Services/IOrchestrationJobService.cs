using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

/// <summary>Tracks coordinator-managed run/debug jobs independently from terminal sessions.</summary>
public interface IOrchestrationJobService
{
    OrchestrationJob Queue(CreateOrchestrationJobRequest request);
    OrchestrationJob? Get(string jobId);
    IReadOnlyList<OrchestrationJob> GetAll();
    OrchestrationJob? UpdateStatus(string jobId, UpdateOrchestrationJobStatusRequest request);
    OrchestrationJob? AppendLog(string jobId, AppendOrchestrationJobLogRequest request);
    OrchestrationJob? RequestCancellation(string jobId, string? message = null);
}
