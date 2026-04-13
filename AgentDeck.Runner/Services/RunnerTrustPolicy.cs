using System.Net;
using AgentDeck.Runner.Configuration;
using Microsoft.Extensions.Options;

namespace AgentDeck.Runner.Services;

/// <inheritdoc />
public sealed class RunnerTrustPolicy : IRunnerTrustPolicy
{
    private readonly TrustPolicyOptions _options;

    public RunnerTrustPolicy(IOptions<TrustPolicyOptions> options)
    {
        _options = options.Value;
    }

    public RunnerTrustDecision Evaluate(
        HttpContext httpContext,
        string action,
        string targetType,
        string? targetId = null,
        string? targetDisplayName = null)
    {
        var actorHeaderName = string.IsNullOrWhiteSpace(_options.ActorHeaderName)
            ? "X-AgentDeck-Actor"
            : _options.ActorHeaderName;
        return Evaluate(RunnerActionContext.FromHttpContext(httpContext, actorHeaderName), action, targetType, targetId, targetDisplayName);
    }

    public RunnerTrustDecision Evaluate(
        RunnerActionContext context,
        string action,
        string targetType,
        string? targetId = null,
        string? targetDisplayName = null)
    {
        string? denialMessage = null;
        if (_options.RequireActorHeaderForPrivilegedActions && string.IsNullOrWhiteSpace(context.ActorId))
        {
            denialMessage = $"The '{_options.ActorHeaderName}' header is required for privileged runner actions.";
        }
        else if (_options.RequireLoopbackForMachineSetup &&
            action is "capability.install" or "capability.update" &&
            !context.IsLoopback &&
            !context.IsCoordinatorBrokered)
        {
            denialMessage = "Machine setup actions are restricted to loopback clients by the current trust policy.";
        }
        else if (_options.RequireLoopbackForDesktopViewerBootstrap &&
            action == "viewer.desktop.create" &&
            !context.IsLoopback &&
            !context.IsCoordinatorBrokered)
        {
            denialMessage = "Desktop viewer bootstrap is restricted to loopback clients by the current trust policy.";
        }

        return new RunnerTrustDecision
        {
            Allowed = denialMessage is null,
            Action = action,
            ActorId = context.ActorId,
            ActorDisplayName = context.ActorDisplayName,
            RemoteAddress = context.RemoteAddress,
            UserAgent = context.UserAgent,
            IsLoopback = context.IsLoopback,
            TargetType = targetType,
            TargetId = targetId,
            TargetDisplayName = targetDisplayName,
            Message = denialMessage
        };
    }
}
