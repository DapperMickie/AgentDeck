using Microsoft.Extensions.DependencyInjection;

namespace AgentDeck.Core.Services;

/// <summary>Extension methods for registering AgentDeck.Core client services.</summary>
public static class CoreClientServiceExtensions
{
    public static IServiceCollection AddAgentDeckCoreClient(this IServiceCollection services)
    {
        services.AddHttpClient();
        services.AddSingleton<ICoordinatorApiClient, CoordinatorApiClient>();
        services.AddSingleton<IAgentDeckClient, AgentDeckClient>();
        services.AddScoped<RemoteViewerRelayClient>();
        return services;
    }
}
