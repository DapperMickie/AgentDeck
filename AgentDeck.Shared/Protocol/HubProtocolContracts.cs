namespace AgentDeck.Shared.Protocol;

/// <summary>
/// SignalR hub protocol values used by AgentDeck clients and services.
/// Hub contracts are additive-only: new DTO fields should be nullable/optional and older peers must ignore them.
/// </summary>
public static class HubProtocolDefaults
{
    public const int CurrentVersion = 1;
    public const int MinimumSupportedVersion = 1;
    public const int MaximumSupportedVersion = 1;
}

/// <summary>Initial per-hub protocol negotiation message sent by the connecting peer.</summary>
public sealed class HubProtocolHello
{
    public int ProtocolVersion { get; init; } = HubProtocolDefaults.CurrentVersion;
    public string? ClientKind { get; init; }
    public string? ClientVersion { get; init; }
    public IReadOnlyList<string>? Capabilities { get; init; }
}

/// <summary>Acknowledgement returned when the peer protocol is supported.</summary>
public sealed class HubProtocolHelloAck
{
    public int ProtocolVersion { get; init; } = HubProtocolDefaults.CurrentVersion;
    public int MinimumSupportedProtocolVersion { get; init; } = HubProtocolDefaults.MinimumSupportedVersion;
    public int MaximumSupportedProtocolVersion { get; init; } = HubProtocolDefaults.MaximumSupportedVersion;
    public string? ServerKind { get; init; }
    public string? ServerVersion { get; init; }
    public IReadOnlyList<string>? Capabilities { get; init; }
}
