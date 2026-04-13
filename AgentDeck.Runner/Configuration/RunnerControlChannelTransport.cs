namespace AgentDeck.Runner.Configuration;

/// <summary>Transport policy for the runner's outbound coordinator control channel.</summary>
public enum RunnerControlChannelTransport
{
    Auto,
    WebSockets,
    LongPolling
}
