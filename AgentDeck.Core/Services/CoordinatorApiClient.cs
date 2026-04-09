using System.Net.Http;
using System.Net.Http.Json;
using AgentDeck.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AgentDeck.Core.Services;

/// <inheritdoc />
public sealed class CoordinatorApiClient : ICoordinatorApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAgentDeckClient _agentDeckClient;
    private readonly ILogger<CoordinatorApiClient> _logger;

    public CoordinatorApiClient(
        IHttpClientFactory httpClientFactory,
        IAgentDeckClient agentDeckClient,
        ILogger<CoordinatorApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _agentDeckClient = agentDeckClient;
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

    public async Task<IReadOnlyList<ProjectDefinition>> GetProjectsAsync(string coordinatorUrl, CancellationToken cancellationToken = default)
    {
        using var httpClient = CreateClient(coordinatorUrl);
        try
        {
            return await httpClient.GetFromJsonAsync<IReadOnlyList<ProjectDefinition>>("api/projects", cancellationToken) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Coordinator project lookup failed for {CoordinatorUrl}", coordinatorUrl);
            throw;
        }
    }

    public async Task<IReadOnlyList<ProjectSessionRecord>> GetProjectSessionsAsync(string coordinatorUrl, string? projectId = null, CancellationToken cancellationToken = default)
    {
        using var httpClient = CreateClient(coordinatorUrl);
        try
        {
            var requestUri = string.IsNullOrWhiteSpace(projectId)
                ? "api/project-sessions"
                : $"api/project-sessions?projectId={Uri.EscapeDataString(projectId)}";
            return await httpClient.GetFromJsonAsync<IReadOnlyList<ProjectSessionRecord>>(requestUri, cancellationToken) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Coordinator project session lookup failed for {CoordinatorUrl}", coordinatorUrl);
            throw;
        }
    }

    public async Task<OpenProjectOnMachineResult?> OpenProjectOnMachineAsync(string coordinatorUrl, string projectId, string machineId, CancellationToken cancellationToken = default)
    {
        await EnsureCompanionIdentityAsync(coordinatorUrl, cancellationToken);
        using var httpClient = CreateClient(coordinatorUrl);
        try
        {
            using var response = await httpClient.PostAsync(
                $"api/projects/{Uri.EscapeDataString(projectId)}/open/{Uri.EscapeDataString(machineId)}",
                content: null,
                cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<OpenProjectOnMachineResult>(cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Coordinator project open failed for {ProjectId} on {MachineId}", projectId, machineId);
            throw;
        }
    }

    public async Task<ProjectSessionRecord?> AttachProjectSessionAsync(string coordinatorUrl, string projectSessionId, CancellationToken cancellationToken = default)
    {
        await EnsureCompanionIdentityAsync(coordinatorUrl, cancellationToken);
        using var httpClient = CreateClient(coordinatorUrl);
        try
        {
            using var response = await httpClient.PostAsync(
                $"api/project-sessions/{Uri.EscapeDataString(projectSessionId)}/attachments",
                content: null,
                cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ProjectSessionRecord>(cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Coordinator project session attach failed for {ProjectSessionId}", projectSessionId);
            throw;
        }
    }

    public async Task<ProjectSessionRecord?> DetachProjectSessionAsync(string coordinatorUrl, string projectSessionId, CancellationToken cancellationToken = default)
    {
        await EnsureCompanionIdentityAsync(coordinatorUrl, cancellationToken);
        using var httpClient = CreateClient(coordinatorUrl);
        try
        {
            using var response = await httpClient.PostAsync(
                $"api/project-sessions/{Uri.EscapeDataString(projectSessionId)}/detach",
                content: null,
                cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ProjectSessionRecord>(cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Coordinator project session detach failed for {ProjectSessionId}", projectSessionId);
            throw;
        }
    }

    public async Task<ProjectSessionRecord?> UpdateProjectSessionControlAsync(string coordinatorUrl, string projectSessionId, UpdateProjectSessionControlRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureCompanionIdentityAsync(coordinatorUrl, cancellationToken);
        using var httpClient = CreateClient(coordinatorUrl);
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"api/project-sessions/{Uri.EscapeDataString(projectSessionId)}/control",
                request,
                cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                var message = await TryReadErrorMessageAsync(response, cancellationToken);
                throw new InvalidOperationException(message ?? $"Project session '{projectSessionId}' rejected the requested control change.");
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ProjectSessionRecord>(cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _logger.LogError(ex, "Coordinator project session control update failed for {ProjectSessionId}", projectSessionId);
            throw;
        }
    }

    private HttpClient CreateClient(string coordinatorUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coordinatorUrl);
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(AppendTrailingSlash(coordinatorUrl.Trim()), UriKind.Absolute);
        if (!string.IsNullOrWhiteSpace(_agentDeckClient.CompanionId))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(AgentDeck.Shared.AgentDeckHeaderNames.Companion, _agentDeckClient.CompanionId);
            client.DefaultRequestHeaders.TryAddWithoutValidation(AgentDeck.Shared.AgentDeckHeaderNames.Actor, _agentDeckClient.CompanionId);
        }

        return client;
    }

    private static string AppendTrailingSlash(string baseUrl) =>
        baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : $"{baseUrl}/";

    private async Task EnsureCompanionIdentityAsync(string coordinatorUrl, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_agentDeckClient.CompanionId))
        {
            return;
        }

        await _agentDeckClient.ConnectAsync(coordinatorUrl, cancellationToken);
    }

    private static async Task<string?> TryReadErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<CoordinatorErrorResponse>(cancellationToken: cancellationToken);
            return payload?.Message;
        }
        catch
        {
            return null;
        }
    }

    private sealed class CoordinatorErrorResponse
    {
        public string? Message { get; init; }
    }
}
