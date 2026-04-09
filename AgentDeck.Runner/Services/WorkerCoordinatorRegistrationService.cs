using System.Net.Http.Json;
using AgentDeck.Runner.Configuration;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;
using Microsoft.Extensions.Options;

namespace AgentDeck.Runner.Services;

public sealed class WorkerCoordinatorRegistrationService : BackgroundService
{
    private readonly WorkerCoordinatorOptions _coordinatorOptions;
    private readonly IMachineCapabilityService _capabilities;
    private readonly ILogger<WorkerCoordinatorRegistrationService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public WorkerCoordinatorRegistrationService(
        IOptions<WorkerCoordinatorOptions> coordinatorOptions,
        IMachineCapabilityService capabilities,
        IHttpClientFactory httpClientFactory,
        ILogger<WorkerCoordinatorRegistrationService> logger)
    {
        _coordinatorOptions = coordinatorOptions.Value;
        _capabilities = capabilities;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_coordinatorOptions.CoordinatorUrl))
        {
            return;
        }

        var coordinatorUrl = AppendTrailingSlash(_coordinatorOptions.CoordinatorUrl);
        var delay = _coordinatorOptions.WorkerHeartbeatInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var httpClient = _httpClientFactory.CreateClient();
                httpClient.BaseAddress = new Uri(coordinatorUrl, UriKind.Absolute);

                var snapshot = await _capabilities.GetSnapshotAsync(stoppingToken);
                var request = new RegisterRunnerMachineRequest
                {
                    MachineId = _coordinatorOptions.MachineId.Trim(),
                    MachineName = _coordinatorOptions.MachineName.Trim(),
                    Role = RunnerMachineRole.Worker,
                    RunnerUrl = string.IsNullOrWhiteSpace(_coordinatorOptions.AdvertisedRunnerUrl)
                        ? null
                        : _coordinatorOptions.AdvertisedRunnerUrl.Trim(),
                    Platform = snapshot.Platform,
                    SupportedTargets = snapshot.SupportedTargets
                };

                using var response = await httpClient.PostAsJsonAsync("api/cluster/workers/register", request, stoppingToken);
                response.EnsureSuccessStatusCode();
                var registration = await response.Content.ReadFromJsonAsync<RegisterRunnerMachineResponse>(cancellationToken: stoppingToken);
                delay = registration?.HeartbeatInterval > TimeSpan.Zero
                    ? registration.HeartbeatInterval
                    : _coordinatorOptions.WorkerHeartbeatInterval;

                _logger.LogInformation(
                    "Registered worker runner {MachineName} with coordinator {CoordinatorUrl}",
                    request.MachineName,
                    coordinatorUrl);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to register worker runner with coordinator {CoordinatorUrl}", coordinatorUrl);
                delay = _coordinatorOptions.WorkerHeartbeatInterval;
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static string AppendTrailingSlash(string baseUrl) =>
        baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : $"{baseUrl}/";
}
