using System.Collections.Concurrent;
using AgentDeck.Coordinator.Configuration;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;
using Microsoft.Extensions.Options;

namespace AgentDeck.Coordinator.Services;

public sealed class WorkerRegistryService : IWorkerRegistryService
{
    private sealed record WorkerEntry(RegisteredRunnerMachine Machine);

    private readonly Lock _lock = new();
    private readonly CoordinatorOptions _coordinatorOptions;
    private readonly IRunnerDefinitionCatalogService _definitions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkerRegistryService> _logger;
    private readonly ConcurrentDictionary<string, WorkerEntry> _workers = new(StringComparer.OrdinalIgnoreCase);

    public WorkerRegistryService(
        IOptions<CoordinatorOptions> coordinatorOptions,
        IRunnerDefinitionCatalogService definitions,
        TimeProvider timeProvider,
        ILogger<WorkerRegistryService> logger)
    {
        _coordinatorOptions = coordinatorOptions.Value;
        _definitions = definitions;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task<IReadOnlyList<RegisteredRunnerMachine>> GetMachinesAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<RegisteredRunnerMachine> machines = RefreshWorkerStates()
                .OrderBy(machine => machine.MachineName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return Task.FromResult(machines);
        }
    }

    public Task<RegisteredRunnerMachine?> GetMachineAsync(string machineId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);

        lock (_lock)
        {
            RefreshWorkerStates();
            var normalizedMachineId = Normalize(machineId);
            var machine = _workers.TryGetValue(normalizedMachineId, out var worker)
                ? worker.Machine
                : null;
            return Task.FromResult(machine);
        }
    }

    public RegisterRunnerMachineResponse RegisterOrUpdateWorker(RegisterRunnerMachineRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MachineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MachineName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentVersion);

        lock (_lock)
        {
            var now = _timeProvider.GetUtcNow();
            var machineId = Normalize(request.MachineId);
            var existing = _workers.TryGetValue(machineId, out var current) ? current.Machine : null;
            var desiredState = BuildDesiredState(request.AgentVersion, request.ProtocolVersion);
            var normalizedMachineName = Normalize(request.MachineName);
            var normalizedAgentVersion = Normalize(request.AgentVersion);
            var normalizedRunnerUrl = string.IsNullOrWhiteSpace(request.RunnerUrl) ? null : request.RunnerUrl.Trim();
            var machine = new RegisteredRunnerMachine
            {
                MachineId = machineId,
                MachineName = normalizedMachineName,
                Role = RunnerMachineRole.Worker,
                AgentVersion = normalizedAgentVersion,
                ProtocolVersion = request.ProtocolVersion,
                WorkflowCatalogVersion = NormalizeOptional(request.WorkflowCatalogVersion),
                SecurityPolicyVersion = desiredState.SecurityPolicy.PolicyVersion,
                DesiredUpdateManifestId = desiredState.DesiredUpdateManifest?.DefinitionId,
                DesiredWorkflowPackId = desiredState.DesiredWorkflowPack?.DefinitionId,
                UpdateStatus = request.UpdateStatus,
                RunnerUrl = normalizedRunnerUrl,
                RegisteredAt = existing?.RegisteredAt ?? now,
                LastSeenAt = now,
                IsOnline = true,
                IsCoordinator = false,
                UpdateAvailable = desiredState.UpdateAvailable,
                ProtocolCompatible = desiredState.ProtocolCompatible,
                Platform = request.Platform,
                SupportedTargets = request.SupportedTargets
                    .Where(target => !string.IsNullOrWhiteSpace(target.DisplayName))
                    .ToArray()
            };

            _workers[machineId] = new WorkerEntry(machine);
            if (existing is null)
            {
                _logger.LogInformation(
                    "Registered worker {MachineName} ({MachineId}) version {AgentVersion} protocol {ProtocolVersion} at {RunnerUrl}",
                    normalizedMachineName,
                    machineId,
                    normalizedAgentVersion,
                    request.ProtocolVersion,
                    normalizedRunnerUrl ?? "<not advertised>");
            }
            else
            {
                _logger.LogInformation(
                    "Updated worker heartbeat for {MachineName} ({MachineId}) version {AgentVersion} protocol {ProtocolVersion} at {RunnerUrl}",
                    normalizedMachineName,
                    machineId,
                    normalizedAgentVersion,
                    request.ProtocolVersion,
                    normalizedRunnerUrl ?? "<not advertised>");
            }
            return new RegisterRunnerMachineResponse
            {
                Machine = machine,
                DesiredState = desiredState,
                HeartbeatInterval = _coordinatorOptions.WorkerHeartbeatInterval
            };
        }
    }

    private IReadOnlyList<RegisteredRunnerMachine> RefreshWorkerStates()
    {
        var now = _timeProvider.GetUtcNow();
        var expiryThreshold = _coordinatorOptions.WorkerExpiry > TimeSpan.Zero
            ? now - _coordinatorOptions.WorkerExpiry
            : DateTimeOffset.MinValue;
        var onlineThreshold = _coordinatorOptions.WorkerHeartbeatInterval > TimeSpan.Zero
            ? now - _coordinatorOptions.WorkerHeartbeatInterval
            : DateTimeOffset.MinValue;
        var machines = new List<RegisteredRunnerMachine>();

        foreach (var worker in _workers.ToArray())
        {
            var machine = worker.Value.Machine;
            if (machine.LastSeenAt < expiryThreshold)
            {
                _logger.LogWarning("Removing expired worker {MachineName} ({MachineId}) last seen at {LastSeenAt}", machine.MachineName, machine.MachineId, machine.LastSeenAt);
                _workers.TryRemove(worker.Key, out _);
                continue;
            }

            var isOnline = machine.LastSeenAt >= onlineThreshold;
            if (machine.IsOnline != isOnline)
            {
                machine = new RegisteredRunnerMachine
                {
                    MachineId = machine.MachineId,
                    MachineName = machine.MachineName,
                    Role = machine.Role,
                    AgentVersion = machine.AgentVersion,
                    ProtocolVersion = machine.ProtocolVersion,
                    WorkflowCatalogVersion = machine.WorkflowCatalogVersion,
                    SecurityPolicyVersion = machine.SecurityPolicyVersion,
                    DesiredUpdateManifestId = machine.DesiredUpdateManifestId,
                    DesiredWorkflowPackId = machine.DesiredWorkflowPackId,
                    UpdateStatus = machine.UpdateStatus,
                    RunnerUrl = machine.RunnerUrl,
                    RegisteredAt = machine.RegisteredAt,
                    LastSeenAt = machine.LastSeenAt,
                    IsOnline = isOnline,
                    IsCoordinator = machine.IsCoordinator,
                    UpdateAvailable = machine.UpdateAvailable,
                    ProtocolCompatible = machine.ProtocolCompatible,
                    Platform = machine.Platform,
                    SupportedTargets = machine.SupportedTargets
                };
                _workers[worker.Key] = new WorkerEntry(machine);
                _logger.LogInformation(
                    "Worker {MachineName} ({MachineId}) is now {State}",
                    machine.MachineName,
                    machine.MachineId,
                    isOnline ? "online" : "offline");
            }

            machines.Add(machine);
        }

        return machines;
    }

    private static string Normalize(string value) => value.Trim();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private RunnerDesiredState BuildDesiredState(string agentVersion, int protocolVersion)
    {
        var normalizedAgentVersion = Normalize(agentVersion);
        var desiredManifest = _definitions.GetDesiredUpdateManifest();
        var desiredWorkflowPack = _definitions.GetDesiredWorkflowPack();
        var desiredVersion = NormalizeOptional(_coordinatorOptions.DesiredRunnerVersion)
            ?? NormalizeOptional(desiredManifest.Version)
            ?? normalizedAgentVersion;
        var workflowCatalogVersion = NormalizeOptional(_coordinatorOptions.WorkflowCatalogVersion);
        var protocolCompatible = protocolVersion >= _coordinatorOptions.MinimumSupportedProtocolVersion &&
                                 protocolVersion <= _coordinatorOptions.MaximumSupportedProtocolVersion;

        return new RunnerDesiredState
        {
            MinimumSupportedProtocolVersion = _coordinatorOptions.MinimumSupportedProtocolVersion,
            MaximumSupportedProtocolVersion = _coordinatorOptions.MaximumSupportedProtocolVersion,
            DesiredRunnerVersion = desiredVersion,
            SecurityPolicy = new RunnerControlPlaneSecurityPolicy
            {
                PolicyVersion = NormalizeOptional(_coordinatorOptions.SecurityPolicy?.PolicyVersion) ?? "1",
                AllowUpdateStaging = _coordinatorOptions.SecurityPolicy?.AllowUpdateStaging ?? true,
                RequireCoordinatorOriginForArtifacts = _coordinatorOptions.SecurityPolicy?.RequireCoordinatorOriginForArtifacts ?? true,
                RequireUpdateArtifactChecksum = _coordinatorOptions.SecurityPolicy?.RequireUpdateArtifactChecksum ?? true,
                RequireSignedUpdateManifest = _coordinatorOptions.SecurityPolicy?.RequireSignedUpdateManifest ?? true,
                RequireManifestProvenance = _coordinatorOptions.SecurityPolicy?.RequireManifestProvenance ?? true,
                TrustedManifestSignerIds = (_coordinatorOptions.SecurityPolicy?.TrustedManifestSigners ?? [])
                    .Where(signer => !string.IsNullOrWhiteSpace(signer.SignerId))
                    .Select(signer => signer.SignerId.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                AllowWorkflowPackExecution = _coordinatorOptions.SecurityPolicy?.AllowWorkflowPackExecution ?? false,
                AllowUpdateApply = _coordinatorOptions.SecurityPolicy?.AllowUpdateApply ?? false
            },
            DesiredUpdateManifest = new RunnerDefinitionReference
            {
                DefinitionId = desiredManifest.ManifestId,
                Version = desiredManifest.Version
            },
            DesiredWorkflowPack = new RunnerDefinitionReference
            {
                DefinitionId = desiredWorkflowPack.PackId,
                Version = desiredWorkflowPack.Version
            },
            WorkflowCatalogVersion = workflowCatalogVersion,
            UpdateAvailable = !string.Equals(normalizedAgentVersion, desiredVersion, StringComparison.OrdinalIgnoreCase),
            ApplyUpdate = (_coordinatorOptions.SecurityPolicy?.AllowUpdateApply ?? false) &&
                _coordinatorOptions.ApplyStagedUpdate &&
                !string.Equals(normalizedAgentVersion, desiredVersion, StringComparison.OrdinalIgnoreCase),
            ProtocolCompatible = protocolCompatible,
            StatusMessage = protocolCompatible
                ? null
                : $"Runner protocol {protocolVersion} is outside the coordinator supported range {_coordinatorOptions.MinimumSupportedProtocolVersion}-{_coordinatorOptions.MaximumSupportedProtocolVersion}."
        };
    }
}
