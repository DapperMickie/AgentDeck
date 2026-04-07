using System.Diagnostics.CodeAnalysis;
using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Represents a remote viewer session that is distinct from orchestration jobs and terminals.</summary>
public sealed class RemoteViewerSession
{
    [SetsRequiredMembers]
    public RemoteViewerSession()
    {
    }

    [SetsRequiredMembers]
    public RemoteViewerSession(RemoteViewerSession other)
    {
        Id = other.Id;
        MachineId = other.MachineId;
        MachineName = other.MachineName;
        JobId = other.JobId;
        Target = new RemoteViewerTarget
        {
            Kind = other.Target.Kind,
            DisplayName = other.Target.DisplayName,
            JobId = other.Target.JobId,
            SessionId = other.Target.SessionId,
            WindowTitle = other.Target.WindowTitle,
            VirtualDeviceId = other.Target.VirtualDeviceId,
            VirtualDeviceProfileId = other.Target.VirtualDeviceProfileId
        };
        Provider = other.Provider;
        Status = other.Status;
        ConnectionUri = other.ConnectionUri;
        AccessToken = other.AccessToken;
        StatusMessage = other.StatusMessage;
        CreatedAt = other.CreatedAt;
        UpdatedAt = other.UpdatedAt;
    }

    public required string Id { get; init; } = string.Empty;
    public string? MachineId { get; init; }
    public string? MachineName { get; init; }
    public string? JobId { get; init; }
    public RemoteViewerTarget Target { get; init; } = new();
    public RemoteViewerProviderKind Provider { get; init; } = RemoteViewerProviderKind.Auto;
    public RemoteViewerSessionStatus Status { get; init; } = RemoteViewerSessionStatus.Requested;
    public string? ConnectionUri { get; init; }
    public string? AccessToken { get; init; }
    public string? StatusMessage { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
