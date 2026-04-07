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
        var actorId = httpContext.Request.Headers[actorHeaderName]
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            ?.Trim();
        var remoteAddress = httpContext.Connection.RemoteIpAddress;
        var remoteAddressText = remoteAddress?.ToString();
        var isLoopback = remoteAddress is null || IPAddress.IsLoopback(remoteAddress);
        var userAgent = httpContext.Request.Headers.UserAgent.ToString();

        string? denialMessage = null;
        if (_options.RequireActorHeaderForPrivilegedActions && string.IsNullOrWhiteSpace(actorId))
        {
            denialMessage = $"The '{actorHeaderName}' header is required for privileged runner actions.";
        }
        else if (_options.RequireLoopbackForMachineSetup &&
            action is "capability.install" or "capability.update" &&
            !isLoopback)
        {
            denialMessage = "Machine setup actions are restricted to loopback clients by the current trust policy.";
        }
        else if (_options.RequireLoopbackForDesktopViewerBootstrap &&
            action == "viewer.desktop.create" &&
            !isLoopback)
        {
            denialMessage = "Desktop viewer bootstrap is restricted to loopback clients by the current trust policy.";
        }

        return new RunnerTrustDecision
        {
            Allowed = denialMessage is null,
            Action = action,
            ActorId = string.IsNullOrWhiteSpace(actorId) ? "anonymous" : actorId,
            ActorDisplayName = string.IsNullOrWhiteSpace(actorId) ? "Anonymous" : actorId,
            RemoteAddress = remoteAddressText,
            UserAgent = string.IsNullOrWhiteSpace(userAgent) ? null : userAgent,
            IsLoopback = isLoopback,
            TargetType = targetType,
            TargetId = targetId,
            TargetDisplayName = targetDisplayName,
            Message = denialMessage
        };
    }
}
