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
    private readonly ConcurrentDictionary<string, MachineUpdateApplyIntentMode> _machineApplyIntentOverrides = new(StringComparer.OrdinalIgnoreCase);

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

    public Task<IReadOnlyList<RunnerUpdateRolloutStatus>> GetUpdateRolloutsAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<RunnerUpdateRolloutStatus> rollouts = RefreshWorkerStates()
                .Select(machine => machine.UpdateRollout)
                .Where(rollout => rollout is not null)
                .Cast<RunnerUpdateRolloutStatus>()
                .OrderBy(rollout => rollout.MachineName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return Task.FromResult(rollouts);
        }
    }

    public Task<RunnerUpdateRolloutStatus?> GetUpdateRolloutAsync(string machineId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);

        lock (_lock)
        {
            RefreshWorkerStates();
            var normalizedMachineId = Normalize(machineId);
            var rollout = _workers.TryGetValue(normalizedMachineId, out var worker)
                ? worker.Machine.UpdateRollout
                : null;
            return Task.FromResult(rollout);
        }
    }

    public Task<RunnerUpdateRolloutStatus?> UpdateMachineApplyIntentAsync(string machineId, MachineUpdateApplyIntentMode mode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);

        lock (_lock)
        {
            RefreshWorkerStates();
            var normalizedMachineId = Normalize(machineId);
            if (!_workers.ContainsKey(normalizedMachineId))
            {
                return Task.FromResult<RunnerUpdateRolloutStatus?>(null);
            }

            if (mode == MachineUpdateApplyIntentMode.Inherit)
            {
                _machineApplyIntentOverrides.TryRemove(normalizedMachineId, out _);
            }
            else
            {
                _machineApplyIntentOverrides[normalizedMachineId] = mode;
            }

            RefreshWorkerStates();
            var rollout = _workers.TryGetValue(normalizedMachineId, out var worker)
                ? worker.Machine.UpdateRollout
                : null;
            return Task.FromResult(rollout);
        }
    }

    public Task<bool> ClearMachineWorkflowPackStatusAsync(string machineId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);

        lock (_lock)
        {
            RefreshWorkerStates();
            var normalizedMachineId = Normalize(machineId);
            if (!_workers.TryGetValue(normalizedMachineId, out var worker))
            {
                return Task.FromResult(false);
            }

            var machine = worker.Machine;
            _workers[normalizedMachineId] = new WorkerEntry(new RegisteredRunnerMachine
            {
                MachineId = machine.MachineId,
                MachineName = machine.MachineName,
                Role = machine.Role,
                AgentVersion = machine.AgentVersion,
                ProtocolVersion = machine.ProtocolVersion,
                WorkflowCatalogVersion = machine.WorkflowCatalogVersion,
                WorkflowCatalogStatus = machine.WorkflowCatalogStatus,
                CapabilityCatalogVersion = machine.CapabilityCatalogVersion,
                CapabilityCatalogStatus = machine.CapabilityCatalogStatus,
                SetupCatalogVersion = machine.SetupCatalogVersion,
                SetupCatalogStatus = machine.SetupCatalogStatus,
                SecurityPolicyVersion = machine.SecurityPolicyVersion,
                DesiredUpdateManifestId = machine.DesiredUpdateManifestId,
                DesiredWorkflowPackId = machine.DesiredWorkflowPackId,
                DesiredCapabilityCatalogId = machine.DesiredCapabilityCatalogId,
                DesiredSetupCatalogId = machine.DesiredSetupCatalogId,
                UpdateStatus = machine.UpdateStatus,
                UpdateRollout = machine.UpdateRollout,
                WorkflowPackStatus = null,
                RunnerUrl = machine.RunnerUrl,
                RegisteredAt = machine.RegisteredAt,
                LastSeenAt = machine.LastSeenAt,
                IsOnline = machine.IsOnline,
                IsCoordinator = machine.IsCoordinator,
                UpdateAvailable = machine.UpdateAvailable,
                ProtocolCompatible = machine.ProtocolCompatible,
                Platform = machine.Platform,
                SupportedTargets = machine.SupportedTargets,
                RemoteViewerProviders = machine.RemoteViewerProviders
            });

            return Task.FromResult(true);
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
            var desiredState = BuildDesiredState(request.MachineId, request.AgentVersion, request.ProtocolVersion);
            var normalizedMachineName = Normalize(request.MachineName);
            var normalizedAgentVersion = Normalize(request.AgentVersion);
            var normalizedRunnerUrl = string.IsNullOrWhiteSpace(request.RunnerUrl) ? null : request.RunnerUrl.Trim();
            var updateRollout = BuildUpdateRollout(
                machineId,
                normalizedMachineName,
                normalizedAgentVersion,
                desiredState,
                request.UpdateStatus,
                isOnline: true);
            var machine = new RegisteredRunnerMachine
            {
                MachineId = machineId,
                MachineName = normalizedMachineName,
                Role = RunnerMachineRole.Worker,
                AgentVersion = normalizedAgentVersion,
                ProtocolVersion = request.ProtocolVersion,
                WorkflowCatalogVersion = NormalizeOptional(request.WorkflowCatalogVersion),
                WorkflowCatalogStatus = request.WorkflowCatalogStatus,
                CapabilityCatalogVersion = NormalizeOptional(request.CapabilityCatalogVersion),
                CapabilityCatalogStatus = request.CapabilityCatalogStatus,
                SetupCatalogVersion = NormalizeOptional(request.SetupCatalogVersion),
                SetupCatalogStatus = request.SetupCatalogStatus,
                SecurityPolicyVersion = desiredState.SecurityPolicy.PolicyVersion,
                DesiredUpdateManifestId = desiredState.DesiredUpdateManifest?.DefinitionId,
                DesiredWorkflowPackId = desiredState.DesiredWorkflowPack?.DefinitionId,
                DesiredCapabilityCatalogId = desiredState.DesiredCapabilityCatalog?.DefinitionId,
                DesiredSetupCatalogId = desiredState.DesiredSetupCatalog?.DefinitionId,
                UpdateStatus = request.UpdateStatus,
                UpdateRollout = updateRollout,
                WorkflowPackStatus = request.WorkflowPackStatus,
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
                    .ToArray(),
                RemoteViewerProviders = request.RemoteViewerProviders
                    .Where(provider => !string.IsNullOrWhiteSpace(provider.DisplayName))
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
                _machineApplyIntentOverrides.TryRemove(worker.Key, out _);
                continue;
            }

            var isOnline = machine.LastSeenAt >= onlineThreshold;
            var desiredState = BuildDesiredState(machine.MachineId, machine.AgentVersion, machine.ProtocolVersion);
            var refreshedMachine = new RegisteredRunnerMachine
            {
                MachineId = machine.MachineId,
                MachineName = machine.MachineName,
                Role = machine.Role,
                AgentVersion = machine.AgentVersion,
                ProtocolVersion = machine.ProtocolVersion,
                WorkflowCatalogVersion = machine.WorkflowCatalogVersion,
                WorkflowCatalogStatus = machine.WorkflowCatalogStatus,
                CapabilityCatalogVersion = machine.CapabilityCatalogVersion,
                CapabilityCatalogStatus = machine.CapabilityCatalogStatus,
                SetupCatalogVersion = machine.SetupCatalogVersion,
                SetupCatalogStatus = machine.SetupCatalogStatus,
                SecurityPolicyVersion = desiredState.SecurityPolicy.PolicyVersion,
                DesiredUpdateManifestId = desiredState.DesiredUpdateManifest?.DefinitionId,
                DesiredWorkflowPackId = desiredState.DesiredWorkflowPack?.DefinitionId,
                DesiredCapabilityCatalogId = desiredState.DesiredCapabilityCatalog?.DefinitionId,
                DesiredSetupCatalogId = desiredState.DesiredSetupCatalog?.DefinitionId,
                UpdateStatus = machine.UpdateStatus,
                UpdateRollout = BuildUpdateRollout(
                    machine.MachineId,
                    machine.MachineName,
                    machine.AgentVersion,
                    desiredState,
                    machine.UpdateStatus,
                    isOnline),
                WorkflowPackStatus = machine.WorkflowPackStatus,
                RunnerUrl = machine.RunnerUrl,
                RegisteredAt = machine.RegisteredAt,
                LastSeenAt = machine.LastSeenAt,
                IsOnline = isOnline,
                IsCoordinator = machine.IsCoordinator,
                UpdateAvailable = desiredState.UpdateAvailable,
                ProtocolCompatible = desiredState.ProtocolCompatible,
                Platform = machine.Platform,
                SupportedTargets = machine.SupportedTargets,
                RemoteViewerProviders = machine.RemoteViewerProviders
            };
            _workers[worker.Key] = new WorkerEntry(refreshedMachine);
            if (machine.IsOnline != isOnline)
            {
                _logger.LogInformation(
                    "Worker {MachineName} ({MachineId}) is now {State}",
                    refreshedMachine.MachineName,
                    refreshedMachine.MachineId,
                    isOnline ? "online" : "offline");
            }

            machines.Add(refreshedMachine);
        }

        return machines;
    }

    private static string Normalize(string value) => value.Trim();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private RunnerDesiredState BuildDesiredState(string machineId, string agentVersion, int protocolVersion)
    {
        var normalizedMachineId = Normalize(machineId);
        var normalizedAgentVersion = Normalize(agentVersion);
        var desiredManifest = _definitions.GetDesiredUpdateManifest();
        var desiredWorkflowPack = _definitions.GetDesiredWorkflowPack();
        var desiredCapabilityCatalog = _definitions.GetDesiredCapabilityCatalog();
        var desiredSetupCatalog = _definitions.GetDesiredSetupCatalog();
        var desiredVersion = NormalizeOptional(_coordinatorOptions.DesiredRunnerVersion)
            ?? NormalizeOptional(desiredManifest.Version)
            ?? normalizedAgentVersion;
        var workflowCatalogVersion = NormalizeOptional(_coordinatorOptions.WorkflowCatalogVersion);
        var capabilityCatalogVersion = NormalizeOptional(desiredCapabilityCatalog.Version);
        var setupCatalogVersion = NormalizeOptional(desiredSetupCatalog.Version);
        var protocolCompatible = protocolVersion >= _coordinatorOptions.MinimumSupportedProtocolVersion &&
                                 protocolVersion <= _coordinatorOptions.MaximumSupportedProtocolVersion;
        var securityPolicyAllowsApply = _coordinatorOptions.SecurityPolicy?.AllowUpdateApply ?? false;
        var updateAvailable = !string.Equals(normalizedAgentVersion, desiredVersion, StringComparison.OrdinalIgnoreCase);
        var applyUpdate = securityPolicyAllowsApply && _coordinatorOptions.ApplyStagedUpdate && updateAvailable;
        if (_machineApplyIntentOverrides.TryGetValue(normalizedMachineId, out var applyIntentOverride))
        {
            applyUpdate = applyIntentOverride switch
            {
                MachineUpdateApplyIntentMode.RequestApply => securityPolicyAllowsApply && updateAvailable,
                MachineUpdateApplyIntentMode.StageOnly => false,
                _ => applyUpdate
            };
        }

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
            DesiredCapabilityCatalog = new RunnerDefinitionReference
            {
                DefinitionId = desiredCapabilityCatalog.CatalogId,
                Version = desiredCapabilityCatalog.Version
            },
            DesiredSetupCatalog = new RunnerDefinitionReference
            {
                DefinitionId = desiredSetupCatalog.CatalogId,
                Version = desiredSetupCatalog.Version
            },
            WorkflowCatalogVersion = workflowCatalogVersion,
            CapabilityCatalogVersion = capabilityCatalogVersion,
            SetupCatalogVersion = setupCatalogVersion,
            UpdateAvailable = updateAvailable,
            ApplyUpdate = applyUpdate,
            ProtocolCompatible = protocolCompatible,
            StatusMessage = protocolCompatible
                ? null
                : $"Runner protocol {protocolVersion} is outside the coordinator supported range {_coordinatorOptions.MinimumSupportedProtocolVersion}-{_coordinatorOptions.MaximumSupportedProtocolVersion}."
        };
    }

    private static RunnerUpdateRolloutStatus BuildUpdateRollout(
        string machineId,
        string machineName,
        string currentVersion,
        RunnerDesiredState desiredState,
        RunnerUpdateStatus? updateStatus,
        bool isOnline)
    {
        var statusMessage = string.Empty;
        string? blockingReason = null;
        string? failureMessage = updateStatus?.FailureMessage;
        var state = RunnerUpdateRolloutState.UpToDate;
        var applyPermitted = desiredState.SecurityPolicy.AllowUpdateApply;
        var applyRequested = desiredState.ApplyUpdate;
        var applyEligible = false;

        if (!desiredState.ProtocolCompatible)
        {
            state = RunnerUpdateRolloutState.Blocked;
            blockingReason = desiredState.StatusMessage ?? "Runner protocol is incompatible with the coordinator.";
            statusMessage = blockingReason;
        }
        else if (!desiredState.UpdateAvailable)
        {
            state = RunnerUpdateRolloutState.UpToDate;
            statusMessage = "Runner already matches the coordinator's desired version.";
        }
        else
        {
            switch (updateStatus?.State ?? RunnerUpdateStageState.None)
            {
                case RunnerUpdateStageState.Failed:
                    state = RunnerUpdateRolloutState.Failed;
                    statusMessage = failureMessage ?? "Runner update failed.";
                    break;
                case RunnerUpdateStageState.Applying:
                    state = RunnerUpdateRolloutState.Applying;
                    statusMessage = "Runner is currently applying the staged update.";
                    break;
                case RunnerUpdateStageState.Applied:
                    state = RunnerUpdateRolloutState.Applied;
                    statusMessage = "Runner reported the update as applied and is expected to restart on the new install.";
                    break;
                case RunnerUpdateStageState.PayloadStaged:
                    if (applyRequested && !applyPermitted)
                    {
                        state = RunnerUpdateRolloutState.Blocked;
                        blockingReason = $"Coordinator security policy {desiredState.SecurityPolicy.PolicyVersion} does not permit update apply.";
                        statusMessage = blockingReason;
                    }
                    else if (applyRequested && isOnline)
                    {
                        state = RunnerUpdateRolloutState.ReadyToApply;
                        applyEligible = true;
                        statusMessage = "Payload is staged and the coordinator has requested apply.";
                    }
                    else if (applyRequested)
                    {
                        state = RunnerUpdateRolloutState.PayloadStaged;
                        statusMessage = "Payload is staged and apply is requested, but the runner is currently offline.";
                    }
                    else
                    {
                        state = RunnerUpdateRolloutState.PayloadStaged;
                        statusMessage = "Payload is staged and ready for a future apply request.";
                    }

                    break;
                case RunnerUpdateStageState.ManifestStaged:
                    if (applyRequested)
                    {
                        state = RunnerUpdateRolloutState.Blocked;
                        blockingReason = "Coordinator requested apply, but the runner only staged the manifest. Payload download must be enabled before apply.";
                        statusMessage = blockingReason;
                    }
                    else
                    {
                        state = RunnerUpdateRolloutState.ManifestStaged;
                        statusMessage = "Manifest is staged. Payload download is not yet complete or is not enabled on this runner.";
                    }

                    break;
                default:
                    if (!desiredState.SecurityPolicy.AllowUpdateStaging)
                    {
                        state = RunnerUpdateRolloutState.Blocked;
                        blockingReason = $"Coordinator security policy {desiredState.SecurityPolicy.PolicyVersion} does not permit update staging.";
                        statusMessage = blockingReason;
                    }
                    else if (applyRequested && !applyPermitted)
                    {
                        state = RunnerUpdateRolloutState.Blocked;
                        blockingReason = $"Coordinator security policy {desiredState.SecurityPolicy.PolicyVersion} does not permit update apply.";
                        statusMessage = blockingReason;
                    }
                    else if (!isOnline)
                    {
                        state = RunnerUpdateRolloutState.UpdateAvailable;
                        statusMessage = "Coordinator assigned a newer runner version, but the runner is currently offline.";
                    }
                    else
                    {
                        state = RunnerUpdateRolloutState.UpdateAvailable;
                        statusMessage = "Coordinator assigned a newer runner version and is waiting for the runner to stage it.";
                    }

                    break;
            }
        }

        return new RunnerUpdateRolloutStatus
        {
            MachineId = machineId,
            MachineName = machineName,
            CurrentVersion = currentVersion,
            DesiredVersion = desiredState.DesiredRunnerVersion,
            DesiredManifestId = desiredState.DesiredUpdateManifest?.DefinitionId,
            DesiredManifestVersion = desiredState.DesiredUpdateManifest?.Version,
            SecurityPolicyVersion = desiredState.SecurityPolicy.PolicyVersion,
            State = state,
            RunnerStageState = updateStatus?.State,
            IsOnline = isOnline,
            UpdateAvailable = desiredState.UpdateAvailable,
            ApplyRequested = applyRequested,
            ApplyPermitted = applyPermitted,
            ApplyEligible = applyEligible,
            StatusMessage = statusMessage,
            BlockingReason = blockingReason,
            FailureMessage = failureMessage,
            StagedAt = updateStatus?.StagedAt,
            ApplyStartedAt = updateStatus?.ApplyStartedAt,
            AppliedAt = updateStatus?.AppliedAt
        };
    }
}
