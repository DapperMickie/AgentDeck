using System.Net;

namespace AgentDeck.Runner.Services;

public sealed class RunnerActionContext
{
    public string ActorId { get; init; } = "anonymous";
    public string ActorDisplayName { get; init; } = "Anonymous";
    public string? RemoteAddress { get; init; }
    public string? UserAgent { get; init; }
    public bool IsLoopback { get; init; }
    public bool IsCoordinatorBrokered { get; init; }

    public static RunnerActionContext FromHttpContext(HttpContext httpContext, string actorHeaderName)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var actorId = httpContext.Request.Headers[actorHeaderName]
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            ?.Trim();
        var remoteAddress = httpContext.Connection.RemoteIpAddress;
        var userAgent = httpContext.Request.Headers.UserAgent.ToString();
        return new RunnerActionContext
        {
            ActorId = string.IsNullOrWhiteSpace(actorId) ? "anonymous" : actorId,
            ActorDisplayName = string.IsNullOrWhiteSpace(actorId) ? "Anonymous" : actorId,
            RemoteAddress = remoteAddress?.ToString(),
            UserAgent = string.IsNullOrWhiteSpace(userAgent) ? null : userAgent,
            IsLoopback = remoteAddress is null || IPAddress.IsLoopback(remoteAddress)
        };
    }

    public static RunnerActionContext ForCoordinator(string actorId) =>
        new()
        {
            ActorId = string.IsNullOrWhiteSpace(actorId) ? "coordinator" : actorId.Trim(),
            ActorDisplayName = string.IsNullOrWhiteSpace(actorId) ? "Coordinator" : actorId.Trim(),
            RemoteAddress = "coordinator-broker",
            UserAgent = "AgentDeck.Coordinator.SignalR",
            IsCoordinatorBrokered = true
        };
}
