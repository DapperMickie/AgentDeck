namespace AgentDeck.Core.Services;

/// <summary>Creates isolated runner clients so the companion app can connect to multiple machines at once.</summary>
public interface IAgentDeckClientFactory
{
    IAgentDeckClient Create();
}
