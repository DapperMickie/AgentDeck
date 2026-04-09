using System.Net.Http;
using System.Net.Http.Json;
using AgentDeck.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AgentDeck.Core.Services;

/// <inheritdoc />
public sealed class CoordinatorApiClient : ICoordinatorApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CoordinatorApiClient> _logger;

    public CoordinatorApiClient(
        IHttpClientFactory httpClientFactory,
        ILogger<CoordinatorApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<bool> CheckHealthAsync(string coordinatorUrl, CancellationToken cancellationToken = default)
    {
        using var httpClient = CreateClient(coordinatorUrl);
        try
        {
            using var response = await httpClient.GetAsync("health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Coordinator health check failed for {CoordinatorUrl}", coordinatorUrl);
            return false;
        }
    }

    public async Task<IReadOnlyList<RegisteredRunnerMachine>> GetMachinesAsync(string coordinatorUrl, CancellationToken cancellationToken = default)
    {
        using var httpClient = CreateClient(coordinatorUrl);
        try
        {
            return await httpClient.GetFromJsonAsync<IReadOnlyList<RegisteredRunnerMachine>>("api/machines", cancellationToken) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Coordinator machine lookup failed for {CoordinatorUrl}", coordinatorUrl);
            throw;
        }
    }

    private HttpClient CreateClient(string coordinatorUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coordinatorUrl);
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(AppendTrailingSlash(coordinatorUrl.Trim()), UriKind.Absolute);
        return client;
    }

    private static string AppendTrailingSlash(string baseUrl) =>
        baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : $"{baseUrl}/";
}
