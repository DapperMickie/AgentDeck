using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

/// <inheritdoc />
public sealed class RunnerAuditService : IRunnerAuditService
{
    private const int MaxRetainedEvents = 200;
    private readonly Lock _gate = new();
    private readonly List<RunnerAuditEvent> _events = [];
    private readonly ILogger<RunnerAuditService> _logger;

    public RunnerAuditService(ILogger<RunnerAuditService> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<RunnerAuditEvent> GetRecent()
    {
        lock (_gate)
        {
            return [.. _events
                .OrderByDescending(entry => entry.Timestamp)
                .ThenByDescending(entry => entry.Id, StringComparer.Ordinal)];
        }
    }

    public RunnerAuditEvent Record(RunnerTrustDecision decision, RunnerAuditOutcome outcome, string? details = null)
    {
        var auditEvent = new RunnerAuditEvent
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = DateTimeOffset.UtcNow,
            Action = decision.Action,
            Outcome = outcome,
            ActorId = decision.ActorId,
            ActorDisplayName = decision.ActorDisplayName,
            RemoteAddress = decision.RemoteAddress,
            UserAgent = decision.UserAgent,
            TargetType = decision.TargetType,
            TargetId = decision.TargetId,
            TargetDisplayName = decision.TargetDisplayName,
            Details = details ?? decision.Message
        };

        lock (_gate)
        {
            _events.Add(auditEvent);
            if (_events.Count > MaxRetainedEvents)
            {
                _events.RemoveRange(0, _events.Count - MaxRetainedEvents);
            }
        }

        _logger.LogInformation(
            "Runner audit {Outcome}: action={Action}, actor={ActorId}, remote={RemoteAddress}, targetType={TargetType}, targetId={TargetId}, details={Details}",
            outcome,
            auditEvent.Action,
            auditEvent.ActorId,
            auditEvent.RemoteAddress ?? "<unknown>",
            auditEvent.TargetType,
            auditEvent.TargetId ?? "<none>",
            auditEvent.Details ?? "<none>");

        return auditEvent;
    }
}
