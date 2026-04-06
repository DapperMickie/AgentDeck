using Microsoft.Extensions.Logging;

namespace AgentDeck.Core.Services;

/// <inheritdoc />
public sealed class AgentDeckClientFactory : IAgentDeckClientFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public AgentDeckClientFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public IAgentDeckClient Create()
    {
        return new AgentDeckClient(_loggerFactory.CreateLogger<AgentDeckClient>());
    }
}
