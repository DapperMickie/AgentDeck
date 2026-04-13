using AgentDeck.Runner.Configuration;
using Microsoft.Extensions.Options;

namespace AgentDeck.Runner.Services;

public sealed class RunnerConnectionUrlResolver : IRunnerConnectionUrlResolver
{
    private readonly WorkerCoordinatorOptions _coordinatorOptions;
    private readonly RunnerOptions _runnerOptions;

    public RunnerConnectionUrlResolver(
        IOptions<WorkerCoordinatorOptions> coordinatorOptions,
        IOptions<RunnerOptions> runnerOptions)
    {
        _coordinatorOptions = coordinatorOptions.Value;
        _runnerOptions = runnerOptions.Value;
    }

    public string ResolveBaseUrl(string? requestBaseUri = null)
    {
        if (Uri.TryCreate(requestBaseUri, UriKind.Absolute, out var requestUri))
        {
            return requestUri.GetLeftPart(UriPartial.Authority);
        }

        if (Uri.TryCreate(_coordinatorOptions.AdvertisedRunnerUrl, UriKind.Absolute, out var advertisedUri))
        {
            return advertisedUri.GetLeftPart(UriPartial.Authority);
        }

        return $"http://localhost:{_runnerOptions.Port}";
    }
}
