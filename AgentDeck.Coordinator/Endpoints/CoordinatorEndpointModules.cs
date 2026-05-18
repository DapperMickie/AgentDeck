using AgentDeck.Coordinator.Hubs;
using AgentDeck.Coordinator.Services;
using System.Runtime.ExceptionServices;
using System.Text;
using AgentDeck.Shared;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

namespace AgentDeck.Coordinator.Endpoints;

public static class CoordinatorEndpointModules
{
    public static WebApplication MapCoordinatorEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));

        app.MapGet("/artifacts/{**artifactPath}", (string artifactPath, ICoordinatorArtifactService artifacts) =>
        {
            var physicalPath = artifacts.TryResolveArtifactPath(artifactPath);
            return physicalPath is null
                ? Results.NotFound()
                : Results.File(physicalPath, "application/octet-stream", enableRangeProcessing: true);
        });

        app.MapPost("/api/companions/register", (RegisterCompanionRequest? request, ICompanionRegistryService companions) =>
            Results.Ok(companions.RegisterCompanion(request ?? new RegisterCompanionRequest())));

        app.MapGet("/api/companions", (ICompanionRegistryService companions) =>
            Results.Ok(companions.GetCompanions()));

        app.MapGet("/api/companions/{companionId}", (string companionId, ICompanionRegistryService companions) =>
            companions.GetCompanion(companionId) is { } companion
                ? Results.Ok(companion)
                : Results.NotFound());

        app.MapGet("/api/projects", (IProjectRegistryService projects) =>
            Results.Ok(projects.GetProjects()));

        app.MapGet("/api/projects/{projectId}", (string projectId, IProjectRegistryService projects) =>
            projects.GetProject(projectId) is { } project
                ? Results.Ok(project)
                : Results.NotFound());

        app.MapGet("/api/project-sessions", (string? projectId, IProjectSessionRegistryService sessions) =>
            Results.Ok(sessions.GetSessions(projectId)));

        app.MapGet("/api/project-sessions/{projectSessionId}", (string projectSessionId, IProjectSessionRegistryService sessions) =>
            sessions.GetSession(projectSessionId) is { } session
                ? Results.Ok(session)
                : Results.NotFound());

        app.MapPost("/api/project-sessions/{projectSessionId}/surfaces", (string projectSessionId, RegisterProjectSessionSurfaceRequest request, IProjectSessionRegistryService sessions) =>
        {
            try
            {
                return Results.Ok(sessions.RegisterSurface(projectSessionId, request));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        });

        app.MapPost("/api/project-sessions/{projectSessionId}/attachments", (string projectSessionId, HttpContext httpContext, IProjectSessionRegistryService sessions) =>
        {
            var companionId = GetCompanionId(httpContext);
            if (string.IsNullOrWhiteSpace(companionId))
            {
                return Results.BadRequest(new { message = "Coordinator companion identity is required to attach to a project session." });
            }

            try
            {
                return Results.Ok(sessions.AttachCompanion(projectSessionId, companionId, viewerOnly: true));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        });

        app.MapPost("/api/project-sessions/{projectSessionId}/detach", (string projectSessionId, HttpContext httpContext, IProjectSessionRegistryService sessions) =>
        {
            var companionId = GetCompanionId(httpContext);
            if (string.IsNullOrWhiteSpace(companionId))
            {
                return Results.BadRequest(new { message = "Coordinator companion identity is required to detach from a project session." });
            }

            try
            {
                return Results.Ok(sessions.DetachCompanion(projectSessionId, companionId));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        });

        app.MapPost("/api/project-sessions/{projectSessionId}/control", (string projectSessionId, UpdateProjectSessionControlRequest? request, HttpContext httpContext, IProjectSessionRegistryService sessions) =>
        {
            var companionId = GetCompanionId(httpContext);
            if (string.IsNullOrWhiteSpace(companionId))
            {
                return Results.BadRequest(new { message = "Coordinator companion identity is required to update project session control." });
            }

            try
            {
                return Results.Ok(sessions.UpdateControl(projectSessionId, companionId, (request ?? new UpdateProjectSessionControlRequest()).Mode));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        });

        app.MapPost("/api/projects/{projectId}/open/{machineId}", (string projectId, string machineId, HttpContext httpContext, IProjectOpenService projectOpen, CancellationToken cancellationToken) =>
            projectOpen.OpenProjectOnMachineAsync(projectId, machineId, httpContext, cancellationToken));

        app.MapPut("/api/projects/{projectId}", (string projectId, ProjectDefinition project, IProjectRegistryService projects) =>
        {
            try
            {
                return Results.Ok(projects.UpsertProject(projectId, project));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        app.MapPut("/api/projects/{projectId}/workspaces/{machineId}", (string projectId, string machineId, ProjectWorkspaceMapping workspace, IProjectRegistryService projects) =>
        {
            try
            {
                var request = new ProjectWorkspaceMapping
                {
                    MachineId = string.IsNullOrWhiteSpace(workspace.MachineId) ? machineId : workspace.MachineId,
                    MachineName = workspace.MachineName,
                    ProjectPath = workspace.ProjectPath,
                    IsPrimary = workspace.IsPrimary
                };

                return Results.Ok(projects.UpsertWorkspace(projectId, request));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        });

        app.MapGet("/api/machines", async (IWorkerRegistryService registry, CancellationToken cancellationToken) =>
            Results.Ok(await registry.GetMachinesAsync(cancellationToken)));

        app.MapGet("/api/updates/rollouts", async (IWorkerRegistryService registry, CancellationToken cancellationToken) =>
            Results.Ok(await registry.GetUpdateRolloutsAsync(cancellationToken)));

        app.MapGet("/api/runner-orchestration", (IRunnerOrchestrationService orchestration) =>
            Results.Ok(orchestration.GetCatalog()));

        app.MapGet("/api/runner-orchestration/providers/{providerId}", (string providerId, IRunnerOrchestrationService orchestration) =>
            orchestration.GetProvider(providerId) is { } provider
                ? Results.Ok(provider)
                : Results.NotFound());

        app.MapGet("/api/runner-orchestration/templates/{templateId}", (string templateId, IRunnerOrchestrationService orchestration) =>
            orchestration.GetTemplate(templateId) is { } template
                ? Results.Ok(template)
                : Results.NotFound());

        app.MapGet("/api/runner-orchestration/instances/{instanceId}", (string instanceId, IRunnerOrchestrationService orchestration) =>
            orchestration.GetInstance(instanceId) is { } instance
                ? Results.Ok(instance)
                : Results.NotFound());

        app.MapGet("/api/runner-orchestration/instances/{instanceId}/events", async (string instanceId, IRunnerOrchestrationService orchestration, CancellationToken cancellationToken) =>
            Results.Ok(await orchestration.GetInstanceEventsAsync(instanceId, cancellationToken)));

        app.MapPost("/api/runner-orchestration/instances", async (CreateRunnerOrchestratorInstanceRequest request, IRunnerOrchestrationService orchestration, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await orchestration.CreateInstanceAsync(request, cancellationToken));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { message = ex.Message });
            }
        });

        app.MapPost("/api/runner-orchestration/instances/{instanceId}/lifecycle/{state}", async (string instanceId, RunnerInstanceLifecycleState state, IRunnerOrchestrationService orchestration, CancellationToken cancellationToken) =>
            await orchestration.UpdateInstanceLifecycleAsync(instanceId, state, cancellationToken) is { } instance
                ? Results.Ok(instance)
                : Results.NotFound());

        app.MapGet("/api/machines/{machineId}/updates/rollout", async (string machineId, IWorkerRegistryService registry, CancellationToken cancellationToken) =>
            await registry.GetUpdateRolloutAsync(machineId, cancellationToken) is { } rollout
                ? Results.Ok(rollout)
                : Results.NotFound());

        app.MapPost("/api/machines/{machineId}/updates/apply-intent", async (string machineId, UpdateMachineUpdateApplyIntentRequest? request, IWorkerRegistryService registry, CancellationToken cancellationToken) =>
            await registry.UpdateMachineApplyIntentAsync(machineId, (request ?? new UpdateMachineUpdateApplyIntentRequest()).Mode, cancellationToken) is { } rollout
                ? Results.Ok(rollout)
                : Results.NotFound());

        app.MapGet("/api/machines/{machineId}/workspace", async (string machineId, HttpContext httpContext, ICompanionRegistryService companions, IRunnerBrokerService runners, CancellationToken cancellationToken) =>
        {
            try
            {
                TrackMachineAttachment(httpContext, companions, machineId);
                var workspace = await runners.GetWorkspaceAsync(machineId, cancellationToken);
                return workspace is null ? Results.NotFound() : Results.Ok(workspace);
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        });

        app.MapGet("/api/machines/{machineId}/capabilities", async (string machineId, HttpContext httpContext, ICompanionRegistryService companions, IRunnerBrokerService runners, CancellationToken cancellationToken) =>
        {
            try
            {
                TrackMachineAttachment(httpContext, companions, machineId);
                var capabilities = await runners.GetMachineCapabilitiesAsync(machineId, cancellationToken);
                return capabilities is null ? Results.NotFound() : Results.Ok(capabilities);
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        });

        app.MapGet("/api/machines/{machineId}/orchestration/jobs", async (string machineId, HttpContext httpContext, ICompanionRegistryService companions, IRunnerBrokerService runners, CancellationToken cancellationToken) =>
        {
            try
            {
                TrackMachineAttachment(httpContext, companions, machineId);
                return Results.Ok(await runners.GetOrchestrationJobsAsync(machineId, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        });

        app.MapPost("/api/machines/{machineId}/orchestration/jobs", async (string machineId, CreateOrchestrationJobRequest request, HttpContext httpContext, ICompanionRegistryService companions, IProjectSessionRegistryService projectSessions, IRunnerBrokerService runners, CancellationToken cancellationToken) =>
        {
            try
            {
                TrackMachineAttachment(httpContext, companions, machineId);
                if (RejectProjectMutationIfViewer(httpContext, projectSessions, request.ProjectId, machineId) is { } rejection)
                {
                    return rejection;
                }

                return Results.Ok(await runners.QueueOrchestrationJobAsync(machineId, request, GetActorId(httpContext), cancellationToken));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/api/machines/{machineId}/orchestration/jobs/{jobId}/cancel", async (string machineId, string jobId, HttpContext httpContext, ICompanionRegistryService companions, IProjectSessionRegistryService projectSessions, IRunnerBrokerService runners, CancellationToken cancellationToken) =>
        {
            try
            {
                TrackMachineAttachment(httpContext, companions, machineId);
                var existingJob = (await runners.GetOrchestrationJobsAsync(machineId, cancellationToken))
                    .FirstOrDefault(job => string.Equals(job.Id, jobId, StringComparison.OrdinalIgnoreCase));
                if (existingJob is null)
                {
                    return Results.NotFound();
                }

                if (RejectProjectMutationIfViewer(httpContext, projectSessions, existingJob.ProjectId, machineId) is { } rejection)
                {
                    return rejection;
                }

                var job = await runners.CancelOrchestrationJobAsync(machineId, jobId, GetActorId(httpContext), cancellationToken);
                return job is null ? Results.NotFound() : Results.Ok(job);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapGet("/api/machines/{machineId}/viewers/sessions", async (string machineId, HttpContext httpContext, ICompanionRegistryService companions, IRunnerBrokerService runners, CancellationToken cancellationToken) =>
        {
            try
            {
                TrackMachineAttachment(httpContext, companions, machineId);
                return Results.Ok(await runners.GetViewerSessionsAsync(machineId, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        });

        app.MapGet("/api/machines/{machineId}/viewers/control", async (string machineId, HttpContext httpContext, ICompanionRegistryService companions, IMachineRemoteControlRegistryService remoteControl, IRunnerBrokerService runners, CancellationToken cancellationToken) =>
        {
            try
            {
                TrackMachineAttachment(httpContext, companions, machineId);
                var gate = remoteControl.GetGate(machineId);
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var state = await ReconcileMachineRemoteControlAsync(machineId, remoteControl, runners, cancellationToken);
                    return state is null ? Results.NotFound() : Results.Ok(state);
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        });

        app.MapPost("/api/machines/{machineId}/viewers/sessions", async (string machineId, CreateMachineViewerSessionRequest? request, HttpContext httpContext, ICompanionRegistryService companions, IMachineRemoteControlRegistryService remoteControl, IRunnerBrokerService runners, CancellationToken cancellationToken) =>
        {
            var companionId = GetCompanionId(httpContext);
            if (string.IsNullOrWhiteSpace(companionId))
            {
                return Results.BadRequest(new { message = "Coordinator companion identity is required to create a remote viewer session." });
            }

            var companion = companions.GetCompanion(companionId);
            if (companion is null)
            {
                return Results.BadRequest(new { message = $"Coordinator does not recognize companion '{companionId}'." });
            }

            request ??= new CreateMachineViewerSessionRequest();

            try
            {
                TrackMachineAttachment(httpContext, companions, machineId);
                var gate = remoteControl.GetGate(machineId);
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var existingState = await ReconcileMachineRemoteControlAsync(machineId, remoteControl, runners, cancellationToken);
                    if (existingState is not null)
                    {
                        var currentControllerIsRequester = string.Equals(existingState.ControllerCompanionId, companionId.Trim(), StringComparison.OrdinalIgnoreCase);
                        if (!currentControllerIsRequester && !request.ForceTakeover)
                        {
                            return Results.Conflict(new { message = BuildRemoteControlConflictMessage(existingState) });
                        }

                        if (!string.IsNullOrWhiteSpace(existingState.ViewerSessionId))
                        {
                            await runners.CloseViewerSessionAsync(machineId, existingState.ViewerSessionId, GetActorId(httpContext), cancellationToken);
                        }
                    }

                    var session = await runners.CreateViewerSessionAsync(machineId, request.Viewer, GetActorId(httpContext), cancellationToken);
                    remoteControl.SetState(new MachineRemoteControlState
                    {
                        MachineId = machineId.Trim(),
                        MachineName = request.Viewer.MachineName,
                        ControllerCompanionId = companion.CompanionId,
                        ControllerDisplayName = companion.DisplayName,
                        ViewerSessionId = session.Id,
                        TargetKind = session.Target.Kind,
                        TargetDisplayName = session.Target.DisplayName,
                        Provider = session.Provider,
                        ViewerStatus = session.Status,
                        ConnectionUri = session.ConnectionUri,
                        StatusMessage = session.StatusMessage,
                        AcquiredAt = DateTimeOffset.UtcNow,
                        UpdatedAt = session.UpdatedAt
                    });
                    return Results.Ok(session);
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/api/machines/{machineId}/viewers/sessions/{viewerSessionId}/close", async (string machineId, string viewerSessionId, HttpContext httpContext, ICompanionRegistryService companions, IMachineRemoteControlRegistryService remoteControl, IRunnerBrokerService runners, CancellationToken cancellationToken) =>
        {
            var companionId = GetCompanionId(httpContext);
            if (string.IsNullOrWhiteSpace(companionId))
            {
                return Results.BadRequest(new { message = "Coordinator companion identity is required to close a remote viewer session." });
            }

            try
            {
                TrackMachineAttachment(httpContext, companions, machineId);
                var gate = remoteControl.GetGate(machineId);
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var existingState = await ReconcileMachineRemoteControlAsync(machineId, remoteControl, runners, cancellationToken);
                    if (existingState is not null &&
                        string.Equals(existingState.ViewerSessionId, viewerSessionId.Trim(), StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(existingState.ControllerCompanionId, companionId.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        return Results.Conflict(new { message = BuildRemoteControlConflictMessage(existingState) });
                    }

                    var session = await runners.CloseViewerSessionAsync(machineId, viewerSessionId, GetActorId(httpContext), cancellationToken);
                    remoteControl.ClearState(machineId, viewerSessionId);
                    return session is null ? Results.NotFound() : Results.Ok(session);
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/api/machines/{machineId}/viewers/sessions/{viewerSessionId}/control", async (string machineId, string viewerSessionId, UpdateProjectSessionControlRequest? request, HttpContext httpContext, ICompanionRegistryService companions, IMachineRemoteControlRegistryService remoteControl, IRunnerBrokerService runners, CancellationToken cancellationToken) =>
        {
            var companionId = GetCompanionId(httpContext);
            if (string.IsNullOrWhiteSpace(companionId))
            {
                return Results.BadRequest(new { message = "Coordinator companion identity is required to update remote viewer control." });
            }

            var companion = companions.GetCompanion(companionId);
            if (companion is null)
            {
                return Results.BadRequest(new { message = $"Coordinator does not recognize companion '{companionId}'." });
            }

            try
            {
                TrackMachineAttachment(httpContext, companions, machineId);
                var gate = remoteControl.GetGate(machineId);
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var mode = (request ?? new UpdateProjectSessionControlRequest()).Mode;
                    var viewers = await runners.GetViewerSessionsAsync(machineId, cancellationToken);
                    var existingState = await ReconcileMachineRemoteControlAsync(machineId, remoteControl, runners, cancellationToken, viewers);
                    var targetViewer = viewers.FirstOrDefault(viewer =>
                        string.Equals(viewer.Id, viewerSessionId.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (targetViewer is null || targetViewer.Status is RemoteViewerSessionStatus.Closed or RemoteViewerSessionStatus.Failed)
                    {
                        return Results.NotFound(new { message = $"Machine '{machineId}' no longer exposes viewer session '{viewerSessionId}'." });
                    }

                    var normalizedCompanionId = companionId.Trim();
                    var sameRequesterControlsMachine = existingState is not null &&
                        string.Equals(existingState.ControllerCompanionId, normalizedCompanionId, StringComparison.OrdinalIgnoreCase);
                    var acquiredAt = existingState is not null &&
                        string.Equals(existingState.ControllerCompanionId, normalizedCompanionId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(existingState.ViewerSessionId, targetViewer.Id, StringComparison.OrdinalIgnoreCase)
                        ? existingState.AcquiredAt
                        : DateTimeOffset.UtcNow;

                    switch (mode)
                    {
                        case ProjectSessionControlRequestMode.Request:
                            if (existingState is not null && !sameRequesterControlsMachine)
                            {
                                return Results.Conflict(new { message = BuildRemoteControlConflictMessage(existingState) });
                            }

                            return Results.Ok(remoteControl.SetState(BuildRemoteControlState(machineId, targetViewer, companion, acquiredAt)));

                        case ProjectSessionControlRequestMode.ForceTakeover:
                            return Results.Ok(remoteControl.SetState(BuildRemoteControlState(machineId, targetViewer, companion, acquiredAt)));

                        case ProjectSessionControlRequestMode.Yield:
                            if (existingState is null ||
                                !string.Equals(existingState.ViewerSessionId, targetViewer.Id, StringComparison.OrdinalIgnoreCase) ||
                                !string.Equals(existingState.ControllerCompanionId, normalizedCompanionId, StringComparison.OrdinalIgnoreCase))
                            {
                                return Results.Conflict(new
                                {
                                    message = $"Companion '{normalizedCompanionId}' does not currently control viewer session '{targetViewer.Id}' on machine '{machineId}'."
                                });
                            }

                            remoteControl.ClearState(machineId, targetViewer.Id);
                            return Results.Ok(new { yielded = true });

                        default:
                            return Results.BadRequest(new { message = $"Remote viewer control mode '{mode}' is not supported." });
                    }
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapGet("/api/machines/{machineId}/virtual-devices/catalogs", async (string machineId, HttpContext httpContext, ICompanionRegistryService companions, IRunnerBrokerService runners, CancellationToken cancellationToken) =>
        {
            try
            {
                TrackMachineAttachment(httpContext, companions, machineId);
                return Results.Ok(await runners.GetVirtualDeviceCatalogsAsync(machineId, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/api/machines/{machineId}/virtual-devices/resolve", async (string machineId, VirtualDeviceLaunchSelection selection, HttpContext httpContext, ICompanionRegistryService companions, IRunnerBrokerService runners, CancellationToken cancellationToken) =>
        {
            try
            {
                TrackMachineAttachment(httpContext, companions, machineId);
                var resolution = await runners.ResolveVirtualDeviceAsync(machineId, selection, cancellationToken);
                return resolution is null ? Results.NotFound() : Results.Ok(resolution);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        app.MapPost("/api/machines/{machineId}/capabilities/{capabilityId}/install", async (string machineId, string capabilityId, MachineCapabilityInstallRequest? request, HttpContext httpContext, ICompanionRegistryService companions, IRunnerBrokerService runners, CancellationToken cancellationToken) =>
        {
            try
            {
                TrackMachineAttachment(httpContext, companions, machineId);
                var result = await runners.InstallMachineCapabilityAsync(machineId, capabilityId, request ?? new MachineCapabilityInstallRequest(), GetActorId(httpContext), cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/api/machines/{machineId}/capabilities/{capabilityId}/update", async (string machineId, string capabilityId, HttpContext httpContext, ICompanionRegistryService companions, IRunnerBrokerService runners, CancellationToken cancellationToken) =>
        {
            try
            {
                TrackMachineAttachment(httpContext, companions, machineId);
                var result = await runners.UpdateMachineCapabilityAsync(machineId, capabilityId, GetActorId(httpContext), cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        });

        app.MapPost("/api/machines/{machineId}/workflow-pack/retry", async (string machineId, HttpContext httpContext, ICompanionRegistryService companions, IRunnerBrokerService runners, IWorkerRegistryService workers, CancellationToken cancellationToken) =>
        {
            try
            {
                TrackMachineAttachment(httpContext, companions, machineId);
                var retried = await runners.RetryMachineWorkflowPackAsync(machineId, GetActorId(httpContext), cancellationToken);
                if (!retried)
                {
                    return Results.NotFound();
                }

                await workers.ClearMachineWorkflowPackStatusAsync(machineId, cancellationToken);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapGet("/api/runner-definitions/update-manifests/{manifestId}", (string manifestId, IRunnerDefinitionCatalogService catalog) =>
            catalog.GetUpdateManifest(manifestId) is { } manifest
                ? Results.Ok(manifest)
                : Results.NotFound());

        app.MapGet("/api/runner-definitions/workflow-packs/{packId}", (string packId, IRunnerDefinitionCatalogService catalog) =>
            catalog.GetWorkflowPack(packId) is { } pack
                ? Results.Ok(pack)
                : Results.NotFound());

        app.MapGet("/api/runner-definitions/capability-catalogs/{catalogId}", (string catalogId, IRunnerDefinitionCatalogService catalog) =>
            catalog.GetCapabilityCatalog(catalogId) is { } capabilityCatalog
                ? Results.Ok(capabilityCatalog)
                : Results.NotFound());

        app.MapGet("/api/runner-definitions/setup-catalogs/{catalogId}", (string catalogId, IRunnerDefinitionCatalogService catalog) =>
            catalog.GetSetupCatalog(catalogId) is { } setupCatalog
                ? Results.Ok(setupCatalog)
                : Results.NotFound());

        app.MapPost("/api/cluster/workers/register", (RegisterRunnerMachineRequest request, IWorkerRegistryService registry) =>
        {
            try
            {
                return Results.Ok(registry.RegisterOrUpdateWorker(request));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        app.MapHub<CoordinatorAgentHub>("/hubs/agent");
        app.MapHub<CoordinatorViewerHub>("/hubs/viewers");
        app.MapHub<CoordinatorRunnerHub>("/hubs/runners");

        return app;
    }

    static string? GetCompanionId(HttpContext httpContext) =>
        httpContext.Request.Headers[AgentDeckHeaderNames.Companion].FirstOrDefault();

    static string GetActorId(HttpContext httpContext) =>
        httpContext.Request.Headers[AgentDeckHeaderNames.Actor].FirstOrDefault()
        ?? GetCompanionId(httpContext)
        ?? "coordinator";

    static void TrackMachineAttachment(HttpContext httpContext, ICompanionRegistryService companions, string machineId)
    {
        var companionId = GetCompanionId(httpContext);
        if (!string.IsNullOrWhiteSpace(companionId))
        {
            companions.AttachMachine(companionId, machineId);
        }
    }

    static IResult? RejectProjectMutationIfViewer(
        HttpContext httpContext,
        IProjectSessionRegistryService projectSessions,
        string projectId,
        string machineId)
    {
        var companionId = GetCompanionId(httpContext);
        if (string.IsNullOrWhiteSpace(companionId))
        {
            return Results.BadRequest(new { message = "Coordinator companion identity is required to mutate a live project session." });
        }

        var existingSession = GetLatestProjectSession(projectSessions, projectId, machineId);
        if (existingSession is null ||
            string.IsNullOrWhiteSpace(existingSession.CompanionId) ||
            string.Equals(existingSession.CompanionId, companionId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Results.Conflict(new
        {
            message = $"Project '{projectId}' on machine '{machineId}' is currently controlled by companion '{existingSession.CompanionId}'. Take control of session '{existingSession.Id}' before mutating it."
        });
    }

    static ProjectSessionRecord? GetLatestProjectSession(
        IProjectSessionRegistryService projectSessions,
        string projectId,
        string machineId)
    {
        var normalizedProjectId = projectId.Trim();
        var normalizedMachineId = machineId.Trim();

        return projectSessions.GetSessions(normalizedProjectId)
            .Where(session =>
                string.Equals(session.ProjectId, normalizedProjectId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(session.MachineId, normalizedMachineId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(session => session.UpdatedAt)
            .FirstOrDefault();
    }

    static string BuildRemoteControlConflictMessage(MachineRemoteControlState state) =>
        $"Machine '{state.MachineName ?? state.MachineId}' is currently remotely controlled by companion '{state.ControllerDisplayName ?? state.ControllerCompanionId}' through viewer session '{state.ViewerSessionId}'. Use force takeover to replace that remote controller.";

    static MachineRemoteControlState BuildRemoteControlState(
        string machineId,
        RemoteViewerSession viewer,
        RegisteredCompanion companion,
        DateTimeOffset acquiredAt) =>
        new()
        {
            MachineId = machineId.Trim(),
            MachineName = viewer.MachineName,
            ControllerCompanionId = companion.CompanionId,
            ControllerDisplayName = companion.DisplayName,
            ViewerSessionId = viewer.Id,
            TargetKind = viewer.Target.Kind,
            TargetDisplayName = viewer.Target.DisplayName,
            Provider = viewer.Provider,
            ViewerStatus = viewer.Status,
            ConnectionUri = viewer.ConnectionUri,
            StatusMessage = viewer.StatusMessage,
            AcquiredAt = acquiredAt,
            UpdatedAt = viewer.UpdatedAt
        };

    static async Task<MachineRemoteControlState?> ReconcileMachineRemoteControlAsync(
        string machineId,
        IMachineRemoteControlRegistryService remoteControl,
        IRunnerBrokerService runners,
        CancellationToken cancellationToken,
        IReadOnlyList<RemoteViewerSession>? viewers = null)
    {
        var state = remoteControl.GetState(machineId);
        if (state is null)
        {
            return null;
        }

        viewers ??= await runners.GetViewerSessionsAsync(machineId, cancellationToken);
        var activeViewer = viewers.FirstOrDefault(viewer =>
            string.Equals(viewer.Id, state.ViewerSessionId, StringComparison.OrdinalIgnoreCase));
        if (activeViewer is null || activeViewer.Status is RemoteViewerSessionStatus.Closed or RemoteViewerSessionStatus.Failed)
        {
            remoteControl.ClearState(machineId, state.ViewerSessionId);
            return null;
        }

        var reconciled = new MachineRemoteControlState
        {
            MachineId = state.MachineId,
            MachineName = activeViewer.MachineName ?? state.MachineName,
            ControllerCompanionId = state.ControllerCompanionId,
            ControllerDisplayName = state.ControllerDisplayName,
            ViewerSessionId = activeViewer.Id,
            TargetKind = activeViewer.Target.Kind,
            TargetDisplayName = activeViewer.Target.DisplayName,
            Provider = activeViewer.Provider,
            ViewerStatus = activeViewer.Status,
            ConnectionUri = activeViewer.ConnectionUri,
            StatusMessage = activeViewer.StatusMessage,
            AcquiredAt = state.AcquiredAt,
            UpdatedAt = activeViewer.UpdatedAt
        };

        return remoteControl.SetState(reconciled);
    }
}
