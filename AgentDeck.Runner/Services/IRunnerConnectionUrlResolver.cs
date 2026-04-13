namespace AgentDeck.Runner.Services;

public interface IRunnerConnectionUrlResolver
{
    string ResolveBaseUrl(string? requestBaseUri = null);
}
