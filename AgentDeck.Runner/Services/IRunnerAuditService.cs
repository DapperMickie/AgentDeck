using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

/// <summary>Stores recent audit events for sensitive runner actions.</summary>
public interface IRunnerAuditService
{
    IReadOnlyList<RunnerAuditEvent> GetRecent();
    RunnerAuditEvent Record(RunnerTrustDecision decision, RunnerAuditOutcome outcome, string? details = null);
}
