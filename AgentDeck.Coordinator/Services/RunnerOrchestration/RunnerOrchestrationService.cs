using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AgentDeck.Coordinator.Configuration;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;
using Microsoft.Extensions.Options;

namespace AgentDeck.Coordinator.Services;

public sealed class RunnerOrchestrationService : IRunnerOrchestrationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<CoordinatorOptions> _options;
    private readonly ILogger<RunnerOrchestrationService> _logger;
    private readonly ConcurrentDictionary<string, RunnerOrchestratorInstance> _instances = new(StringComparer.OrdinalIgnoreCase);

    public RunnerOrchestrationService(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<CoordinatorOptions> options,
        ILogger<RunnerOrchestrationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public RunnerOrchestratorCatalog GetCatalog() => new()
    {
        Providers = BuildProviders(),
        Templates = BuildTemplates(),
        Instances = _instances.Values.OrderByDescending(instance => instance.UpdatedAt).ToArray()
    };

    public RunnerOrchestratorProvider? GetProvider(string providerId) =>
        BuildProviders().FirstOrDefault(provider => string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase));

    public RunnerOrchestratorTemplate? GetTemplate(string templateId) =>
        BuildTemplates().FirstOrDefault(template => string.Equals(template.Id, templateId, StringComparison.OrdinalIgnoreCase));

    public RunnerOrchestratorInstance? GetInstance(string instanceId) =>
        _instances.TryGetValue(instanceId, out var instance) ? instance : null;

    public async Task<RunnerOrchestratorInstance> CreateInstanceAsync(CreateRunnerOrchestratorInstanceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TemplateId);

        var template = GetTemplate(request.TemplateId) ?? throw new ArgumentException($"Runner template '{request.TemplateId}' was not found.", nameof(request));
        var provider = GetProvider(template.ProviderId) ?? throw new InvalidOperationException($"Runner provider '{template.ProviderId}' was not found.");
        if (!provider.Enabled)
        {
            throw new InvalidOperationException($"Runner provider '{provider.Name}' is disabled.");
        }

        var now = DateTimeOffset.UtcNow;
        var instanceId = $"runner-{Guid.NewGuid():N}";
        var machineId = $"agentdeck-{instanceId}";
        var instanceName = string.IsNullOrWhiteSpace(request.Name) ? template.Name : request.Name.Trim();
        var instance = new RunnerOrchestratorInstance
        {
            Id = instanceId,
            ProviderId = provider.Id,
            TemplateId = template.Id,
            Name = instanceName,
            MachineId = machineId,
            HostPlatform = template.HostPlatform,
            LifecyclePolicy = request.LifecyclePolicy,
            State = RunnerInstanceLifecycleState.Creating,
            StatusMessage = $"Creating {template.Name} through {provider.Name}.",
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = request.LifecyclePolicy == RunnerInstanceLifecyclePolicy.Persistent ? null : now.Add(template.MaxLifetime ?? TimeSpan.FromHours(8)),
            Events = [CreateEvent("Runner request accepted by coordinator.")]
        };
        _instances[instance.Id] = instance;

        try
        {
            if (provider.Kind == RunnerOrchestratorProviderKind.Portainer && provider.Configured)
            {
                instance = await CreatePortainerContainerAsync(provider, template, instance, request, cancellationToken);
            }
            else
            {
                instance = WithEvent(instance, RunnerInstanceLifecycleState.Failed,
                    provider.Kind == RunnerOrchestratorProviderKind.Portainer
                        ? "Portainer provider is not configured. Add EndpointUrl, ApiToken, and DefaultEnvironmentId in coordinator configuration."
                        : $"Provider kind {provider.Kind} is modeled but no adapter implementation is available yet.",
                    level: "warning");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create runner instance {InstanceId} with provider {ProviderId}", instance.Id, provider.Id);
            instance = WithEvent(instance, RunnerInstanceLifecycleState.Failed, ex.Message, level: "error");
        }

        _instances[instance.Id] = instance;
        return instance;
    }

    public async Task<RunnerOrchestratorInstance?> UpdateInstanceLifecycleAsync(string instanceId, RunnerInstanceLifecycleState requestedState, CancellationToken cancellationToken = default)
    {
        if (!_instances.TryGetValue(instanceId, out var instance))
        {
            return null;
        }

        var provider = GetProvider(instance.ProviderId);
        if (provider?.Kind == RunnerOrchestratorProviderKind.Portainer && provider.Configured && !string.IsNullOrWhiteSpace(instance.ProviderResourceId))
        {
            try
            {
                if (requestedState is RunnerInstanceLifecycleState.Stopped or RunnerInstanceLifecycleState.Deleting or RunnerInstanceLifecycleState.Deleted)
                {
                    await StopPortainerContainerAsync(provider, instance.ProviderResourceId, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update Portainer lifecycle for runner instance {InstanceId}", instanceId);
                instance = WithEvent(instance, RunnerInstanceLifecycleState.Failed, ex.Message, level: "error");
                _instances[instance.Id] = instance;
                return instance;
            }
        }

        var message = requestedState switch
        {
            RunnerInstanceLifecycleState.Stopped => "Runner instance stopped.",
            RunnerInstanceLifecycleState.Deleting or RunnerInstanceLifecycleState.Deleted => "Runner instance marked for deletion.",
            RunnerInstanceLifecycleState.Idle => "Runner instance marked idle.",
            _ => $"Runner instance moved to {requestedState}."
        };
        instance = WithEvent(instance, requestedState, message);
        _instances[instance.Id] = instance;
        return instance;
    }

    public Task<IReadOnlyList<RunnerOrchestratorInstanceEvent>> GetInstanceEventsAsync(string instanceId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RunnerOrchestratorInstanceEvent>>(
            _instances.TryGetValue(instanceId, out var instance) ? instance.Events : []);

    private IReadOnlyList<RunnerOrchestratorProvider> BuildProviders()
    {
        var configured = _options.CurrentValue.RunnerOrchestration.Providers;
        var providers = configured.Count > 0
            ? configured
            : [new CoordinatorRunnerOrchestratorProviderOptions
            {
                Id = "portainer-local",
                Name = "Portainer",
                Kind = RunnerOrchestratorProviderKind.Portainer,
                Description = "Create Linux AgentDeck runner containers through Portainer.",
                SecretReference = "Coordinator:RunnerOrchestration:Providers:0:ApiToken"
            }];

        return providers.Select(provider => new RunnerOrchestratorProvider
        {
            Id = provider.Id,
            Name = provider.Name,
            Kind = provider.Kind,
            Enabled = provider.Enabled,
            Description = provider.Description,
            EndpointUrl = provider.EndpointUrl,
            SecretReference = provider.SecretReference,
            DefaultEnvironmentId = provider.DefaultEnvironmentId,
            Configured = provider.Kind != RunnerOrchestratorProviderKind.Portainer ||
                (!string.IsNullOrWhiteSpace(provider.EndpointUrl) && !string.IsNullOrWhiteSpace(provider.ApiToken) && !string.IsNullOrWhiteSpace(provider.DefaultEnvironmentId)),
            StatusMessage = string.IsNullOrWhiteSpace(provider.EndpointUrl)
                ? "Provider configured as a template; endpoint and secret are still required before it can create runners."
                : "Provider configured.",
            Capabilities = provider.Kind == RunnerOrchestratorProviderKind.Portainer
                ? new RunnerOrchestratorProviderCapability
                {
                    SupportsLinux = true,
                    SupportsContainers = true,
                    SupportsVolumes = true,
                    ViewerProviders = [RemoteViewerProviderKind.Managed, RemoteViewerProviderKind.Vnc]
                }
                : new RunnerOrchestratorProviderCapability()
        }).ToArray();
    }

    private IReadOnlyList<RunnerOrchestratorTemplate> BuildTemplates()
    {
        var configured = _options.CurrentValue.RunnerOrchestration.Templates;
        var templates = configured.Count > 0
            ? configured
            : [new CoordinatorRunnerOrchestratorTemplateOptions
            {
                Id = "linux-portainer-runner",
                ProviderId = "portainer-local",
                Name = "Linux Portainer runner",
                Description = "Default ephemeral Linux container runner created through Portainer."
            }];

        return templates.Select(template => new RunnerOrchestratorTemplate
        {
            Id = template.Id,
            ProviderId = template.ProviderId,
            Name = template.Name,
            Description = template.Description,
            HostPlatform = template.HostPlatform,
            Architecture = template.Architecture,
            Image = template.Image,
            WorkspaceRoot = template.WorkspaceRoot,
            WorkspaceVolume = template.WorkspaceVolume,
            RunnerPort = template.RunnerPort,
            NetworkName = template.NetworkName,
            ManagedViewerEnabled = template.ManagedViewerEnabled,
            DefaultLifecyclePolicy = template.DefaultLifecyclePolicy,
            IdleTimeout = template.IdleTimeout,
            MaxLifetime = template.MaxLifetime,
            CapabilityProfile = template.CapabilityProfile
        }).ToArray();
    }

    private async Task<RunnerOrchestratorInstance> CreatePortainerContainerAsync(
        RunnerOrchestratorProvider provider,
        RunnerOrchestratorTemplate template,
        RunnerOrchestratorInstance instance,
        CreateRunnerOrchestratorInstanceRequest request,
        CancellationToken cancellationToken)
    {
        var optionsProvider = _options.CurrentValue.RunnerOrchestration.Providers
            .First(source => string.Equals(source.Id, provider.Id, StringComparison.OrdinalIgnoreCase));
        var baseUrl = optionsProvider.EndpointUrl!.TrimEnd('/') + "/";
        var endpointId = optionsProvider.DefaultEnvironmentId!;
        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
        httpClient.DefaultRequestHeaders.Add("X-API-Key", optionsProvider.ApiToken!);

        instance = WithEvent(instance, RunnerInstanceLifecycleState.Booting, "Requesting Portainer container creation.");
        _instances[instance.Id] = instance;

        var env = new Dictionary<string, string>(request.Environment, StringComparer.OrdinalIgnoreCase)
        {
            ["Coordinator__MachineId"] = instance.MachineId ?? instance.Id,
            ["Coordinator__MachineName"] = instance.Name,
            ["Coordinator__CoordinatorUrl"] = request.CoordinatorUrl ?? _options.CurrentValue.PublicBaseUrl ?? "http://host.docker.internal:5001",
            ["Runner__WorkspaceRoot"] = template.WorkspaceRoot,
            ["Runner__Port"] = template.RunnerPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["DesktopViewerTransport__Managed__Enabled"] = template.ManagedViewerEnabled ? "true" : "false"
        };

        var createRequest = new PortainerContainerCreateRequest
        {
            Image = template.Image,
            Env = env.Select(pair => $"{pair.Key}={pair.Value}").ToArray(),
            ExposedPorts = new Dictionary<string, object> { [$"{template.RunnerPort}/tcp"] = new() },
            HostConfig = new PortainerHostConfig
            {
                NetworkMode = template.NetworkName,
                Binds = string.IsNullOrWhiteSpace(template.WorkspaceVolume)
                    ? []
                    : [$"{template.WorkspaceVolume}:{template.WorkspaceRoot}"],
                PortBindings = new Dictionary<string, IReadOnlyList<PortainerPortBinding>>
                {
                    [$"{template.RunnerPort}/tcp"] = [new PortainerPortBinding { HostPort = string.Empty }]
                },
                RestartPolicy = new PortainerRestartPolicy
                {
                    Name = request.LifecyclePolicy == RunnerInstanceLifecyclePolicy.Ephemeral ? "no" : "unless-stopped"
                }
            },
            Labels = new Dictionary<string, string>
            {
                ["agentdeck.managed"] = "true",
                ["agentdeck.instanceId"] = instance.Id,
                ["agentdeck.templateId"] = template.Id,
                ["agentdeck.lifecycle"] = request.LifecyclePolicy.ToString()
            }
        };

        using var createResponse = await httpClient.PostAsJsonAsync(
            $"api/endpoints/{Uri.EscapeDataString(endpointId)}/docker/containers/create?name={Uri.EscapeDataString(instance.Id)}",
            createRequest,
            cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
        {
            var error = await createResponse.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Portainer rejected runner creation with HTTP {(int)createResponse.StatusCode}: {error}");
        }

        var createResult = await createResponse.Content.ReadFromJsonAsync<PortainerContainerCreateResult>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Portainer returned an empty container creation response.");
        instance = instance.WithResource(createResult.Id, $"Portainer container {createResult.Id} created; starting it now.", RunnerInstanceLifecycleState.Booting);
        _instances[instance.Id] = instance;

        using var startResponse = await httpClient.PostAsync(
            $"api/endpoints/{Uri.EscapeDataString(endpointId)}/docker/containers/{Uri.EscapeDataString(createResult.Id)}/start",
            content: null,
            cancellationToken);
        if (!startResponse.IsSuccessStatusCode)
        {
            var error = await startResponse.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Portainer created the container but could not start it with HTTP {(int)startResponse.StatusCode}: {error}");
        }

        return WithEvent(instance, RunnerInstanceLifecycleState.Registering,
            "Runner container started. Waiting for it to register with the coordinator.");
    }

    private async Task StopPortainerContainerAsync(RunnerOrchestratorProvider provider, string containerId, CancellationToken cancellationToken)
    {
        var optionsProvider = _options.CurrentValue.RunnerOrchestration.Providers
            .First(source => string.Equals(source.Id, provider.Id, StringComparison.OrdinalIgnoreCase));
        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.BaseAddress = new Uri(optionsProvider.EndpointUrl!.TrimEnd('/') + "/", UriKind.Absolute);
        httpClient.DefaultRequestHeaders.Add("X-API-Key", optionsProvider.ApiToken!);
        using var response = await httpClient.PostAsync(
            $"api/endpoints/{Uri.EscapeDataString(optionsProvider.DefaultEnvironmentId!)}/docker/containers/{Uri.EscapeDataString(containerId)}/stop",
            content: null,
            cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotModified)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Portainer could not stop runner container with HTTP {(int)response.StatusCode}: {error}");
        }
    }

    private static RunnerOrchestratorInstance WithEvent(RunnerOrchestratorInstance instance, RunnerInstanceLifecycleState state, string message, string level = "info") => new()
    {
        Id = instance.Id,
        ProviderId = instance.ProviderId,
        TemplateId = instance.TemplateId,
        Name = instance.Name,
        State = state,
        LifecyclePolicy = instance.LifecyclePolicy,
        HostPlatform = instance.HostPlatform,
        MachineId = instance.MachineId,
        ProviderResourceId = instance.ProviderResourceId,
        EndpointUrl = instance.EndpointUrl,
        StatusMessage = message,
        CreatedAt = instance.CreatedAt,
        UpdatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = instance.ExpiresAt,
        Events = instance.Events.Concat([CreateEvent(message, level)]).ToArray()
    };

    private static RunnerOrchestratorInstanceEvent CreateEvent(string message, string level = "info") => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        Message = message,
        Level = level
    };

    private sealed class PortainerContainerCreateRequest
    {
        public string Image { get; init; } = string.Empty;
        public IReadOnlyList<string> Env { get; init; } = [];
        public IReadOnlyDictionary<string, object> ExposedPorts { get; init; } = new Dictionary<string, object>();
        public PortainerHostConfig HostConfig { get; init; } = new();
        public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
    }

    private sealed class PortainerHostConfig
    {
        public string? NetworkMode { get; init; }
        public IReadOnlyList<string> Binds { get; init; } = [];
        public IReadOnlyDictionary<string, IReadOnlyList<PortainerPortBinding>> PortBindings { get; init; } = new Dictionary<string, IReadOnlyList<PortainerPortBinding>>();
        public PortainerRestartPolicy RestartPolicy { get; init; } = new();
    }

    private sealed class PortainerPortBinding
    {
        public string HostIp { get; init; } = string.Empty;
        public string HostPort { get; init; } = string.Empty;
    }

    private sealed class PortainerRestartPolicy
    {
        public string Name { get; init; } = "no";
    }

    private sealed class PortainerContainerCreateResult
    {
        [JsonPropertyName("Id")]
        public string Id { get; init; } = string.Empty;
    }
}

file static class RunnerOrchestratorInstanceExtensions
{
    public static RunnerOrchestratorInstance WithResource(this RunnerOrchestratorInstance instance, string resourceId, string message, RunnerInstanceLifecycleState state) => new()
    {
        Id = instance.Id,
        ProviderId = instance.ProviderId,
        TemplateId = instance.TemplateId,
        Name = instance.Name,
        State = state,
        LifecyclePolicy = instance.LifecyclePolicy,
        HostPlatform = instance.HostPlatform,
        MachineId = instance.MachineId,
        ProviderResourceId = resourceId,
        EndpointUrl = instance.EndpointUrl,
        StatusMessage = message,
        CreatedAt = instance.CreatedAt,
        UpdatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = instance.ExpiresAt,
        Events = instance.Events.Concat([new RunnerOrchestratorInstanceEvent { Message = message }]).ToArray()
    };
}
