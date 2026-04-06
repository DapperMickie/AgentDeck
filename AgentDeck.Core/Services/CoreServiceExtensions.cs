using Microsoft.Extensions.DependencyInjection;

namespace AgentDeck.Core.Services;

/// <summary>Extension methods for registering AgentDeck.Core services.</summary>
public static class CoreServiceExtensions
{
    public static IServiceCollection AddAgentDeckCore(this IServiceCollection services)
    {
        services.AddSingleton<IAgentDeckClient, AgentDeckClient>();
        services.AddSingleton<ISessionStateService, SessionStateService>();
        services.AddSingleton<IConnectionSettingsService, ConnectionSettingsService>();
        services.AddSingleton<IWorkloadCatalogService, WorkloadCatalogService>();
        services.AddSingleton<IWorkloadContainerCommandService, WorkloadContainerCommandService>();
        services.AddSingleton<AppInitializer>();
        services.AddScoped<TerminalInterop>();
        services.AddSingleton<ICliPresetService, CliPresetService>();
        services.AddSingleton<IToastService, ToastService>();
        services.AddScoped<IActiveSessionService, ActiveSessionService>();
        return services;
    }
}
