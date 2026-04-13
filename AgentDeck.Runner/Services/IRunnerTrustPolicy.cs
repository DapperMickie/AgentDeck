using Microsoft.AspNetCore.Http;

namespace AgentDeck.Runner.Services;

/// <summary>Evaluates whether a request is allowed to perform a privileged runner action.</summary>
public interface IRunnerTrustPolicy
{
    RunnerTrustDecision Evaluate(HttpContext httpContext, string action, string targetType, string? targetId = null, string? targetDisplayName = null);
    RunnerTrustDecision Evaluate(RunnerActionContext context, string action, string targetType, string? targetId = null, string? targetDisplayName = null);
}
