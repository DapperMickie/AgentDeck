using System.Reflection;
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
    private readonly IRunnerUpdateStagingService _updateStaging;
    private readonly ILogger<WorkerCoordinatorRegistrationService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public WorkerCoordinatorRegistrationService(
        IOptions<WorkerCoordinatorOptions> coordinatorOptions,
        IMachineCapabilityService capabilities,
        IRunnerUpdateStagingService updateStaging,
        IHttpClientFactory httpClientFactory,
        ILogger<WorkerCoordinatorRegistrationService> logger)
    {
        _coordinatorOptions = coordinatorOptions.Value;
        _capabilities = capabilities;
        _updateStaging = updateStaging;
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
        if (!TryValidateCoordinatorUrl(coordinatorUrl, _coordinatorOptions.AllowInsecureHttpCoordinatorForLoopback, out var validationMessage))
        {
            _logger.LogError("Runner coordinator registration disabled: {Message}", validationMessage);
            return;
        }

        var agentVersion = GetAgentVersion();
        var delay = _coordinatorOptions.WorkerHeartbeatInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var httpClient = _httpClientFactory.CreateClient();
                httpClient.BaseAddress = new Uri(coordinatorUrl, UriKind.Absolute);

                var snapshot = await _capabilities.GetSnapshotAsync(stoppingToken);
                var updateStatus = await _updateStaging.GetCurrentStatusAsync(stoppingToken);
                var request = new RegisterRunnerMachineRequest
                {
                    MachineId = _coordinatorOptions.MachineId.Trim(),
                    MachineName = _coordinatorOptions.MachineName.Trim(),
                    Role = RunnerMachineRole.Worker,
                    AgentVersion = agentVersion,
                    ProtocolVersion = _coordinatorOptions.ProtocolVersion,
                    WorkflowCatalogVersion = "1",
                    UpdateStatus = updateStatus,
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

                if (registration?.DesiredState is { } desiredState)
                {
                    if (!desiredState.ProtocolCompatible)
                    {
                        _logger.LogWarning(
                            "Coordinator {CoordinatorUrl} reported unsupported protocol for runner {MachineName}: local protocol {ProtocolVersion}, supported range {MinimumSupportedProtocolVersion}-{MaximumSupportedProtocolVersion}",
                            coordinatorUrl,
                            request.MachineName,
                            request.ProtocolVersion,
                            desiredState.MinimumSupportedProtocolVersion,
                            desiredState.MaximumSupportedProtocolVersion);
                    }

                    if (desiredState.UpdateAvailable)
                    {
                        _logger.LogInformation(
                            "Coordinator {CoordinatorUrl} requested runner {MachineName} update from {CurrentVersion} to {DesiredVersion} via manifest {ManifestId}",
                            coordinatorUrl,
                            request.MachineName,
                            request.AgentVersion,
                            desiredState.DesiredRunnerVersion,
                            desiredState.DesiredUpdateManifest?.DefinitionId);
                    }

                    if (desiredState.DesiredWorkflowPack is { } desiredWorkflowPack)
                    {
                        if (desiredState.SecurityPolicy.AllowWorkflowPackExecution)
                        {
                            _logger.LogInformation(
                                "Coordinator {CoordinatorUrl} assigned workflow pack {WorkflowPackId}@{WorkflowPackVersion} to runner {MachineName}",
                                coordinatorUrl,
                                desiredWorkflowPack.DefinitionId,
                                desiredWorkflowPack.Version,
                                request.MachineName);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "Coordinator {CoordinatorUrl} assigned workflow pack {WorkflowPackId}@{WorkflowPackVersion} to runner {MachineName}, but workflow execution remains disabled by security policy {PolicyVersion}",
                                coordinatorUrl,
                                desiredWorkflowPack.DefinitionId,
                                desiredWorkflowPack.Version,
                                request.MachineName,
                                desiredState.SecurityPolicy.PolicyVersion);
                        }
                    }

                    await _updateStaging.ReconcileDesiredUpdateAsync(httpClient, desiredState, stoppingToken);
                }

                _logger.LogInformation(
                    "Registered worker runner {MachineName} v{AgentVersion} (protocol {ProtocolVersion}) with coordinator {CoordinatorUrl}",
                    request.MachineName,
                    request.AgentVersion,
                    request.ProtocolVersion,
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

    private static string GetAgentVersion()
    {
        var assembly = typeof(WorkerCoordinatorRegistrationService).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Trim();
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0-dev";
    }

    private static bool TryValidateCoordinatorUrl(string coordinatorUrl, bool allowInsecureHttpCoordinatorForLoopback, out string validationMessage)
    {
        if (!Uri.TryCreate(coordinatorUrl, UriKind.Absolute, out var coordinatorUri))
        {
            validationMessage = $"Configured coordinator URL '{coordinatorUrl}' is not a valid absolute URI.";
            return false;
        }

        if (string.Equals(coordinatorUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            validationMessage = string.Empty;
            return true;
        }

        if (!string.Equals(coordinatorUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            validationMessage = $"Configured coordinator URL '{coordinatorUrl}' must use HTTP or HTTPS.";
            return false;
        }

        if (allowInsecureHttpCoordinatorForLoopback && coordinatorUri.IsLoopback)
        {
            validationMessage = string.Empty;
            return true;
        }

        validationMessage = $"Configured coordinator URL '{coordinatorUrl}' must use HTTPS unless it targets loopback and 'Coordinator:AllowInsecureHttpCoordinatorForLoopback' is enabled.";
        return false;
    }
}
