using AgentDeck.Coordinator.Configuration;
using AgentDeck.Coordinator.Hubs;
using AgentDeck.Coordinator.Services;
using System.Text;
using System.Runtime.ExceptionServices;
using AgentDeck.Shared;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

var builder = WebApplication.CreateBuilder(args);

var coordinatorOptions = builder.Configuration
    .GetSection(CoordinatorOptions.SectionName)
    .Get<CoordinatorOptions>() ?? new CoordinatorOptions();

builder.WebHost.UseUrls($"http://0.0.0.0:{coordinatorOptions.Port}");

builder.Services.AddOptions<CoordinatorOptions>()
    .Bind(builder.Configuration.GetSection(CoordinatorOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient();
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.KeepAliveInterval = coordinatorOptions.RunnerControlKeepAliveInterval;
    options.ClientTimeoutInterval = coordinatorOptions.RunnerControlClientTimeoutInterval;
    options.HandshakeTimeout = coordinatorOptions.RunnerControlHandshakeTimeout;
});
builder.Services.AddSingleton<ICoordinatorArtifactService, CoordinatorArtifactService>();
builder.Services.AddSingleton<IRunnerDefinitionCatalogService, RunnerDefinitionCatalogService>();
builder.Services.AddSingleton<IWorkerRegistryService, WorkerRegistryService>();
builder.Services.AddSingleton<ICompanionRegistryService, CompanionRegistryService>();
builder.Services.AddSingleton<IProjectRegistryService, ProjectRegistryService>();
builder.Services.AddSingleton<IProjectSessionRegistryService, ProjectSessionRegistryService>();
builder.Services.AddSingleton<IMachineRemoteControlRegistryService, MachineRemoteControlRegistryService>();
builder.Services.AddSingleton<RunnerBrokerService>();
builder.Services.AddSingleton<IRunnerBrokerService>(static services => services.GetRequiredService<RunnerBrokerService>());

var app = builder.Build();

app.Logger.LogInformation(
    "Starting AgentDeck coordinator on port {Port} with worker heartbeat {HeartbeatInterval} and expiry {WorkerExpiry}",
    coordinatorOptions.Port,
    coordinatorOptions.WorkerHeartbeatInterval,
    coordinatorOptions.WorkerExpiry);

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

app.MapPost("/api/projects/{projectId}/open/{machineId}", async (string projectId, string machineId, HttpContext httpContext, ICompanionRegistryService companions, IProjectRegistryService projects, IProjectSessionRegistryService projectSessions, IWorkerRegistryService registry, IRunnerBrokerService runners, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
{
    try
    {
        var logger = loggerFactory.CreateLogger("ProjectOpenFlow");
        TrackMachineAttachment(httpContext, companions, machineId);
        var companionId = GetCompanionId(httpContext)?.Trim();

        var project = projects.GetProject(projectId);
        if (project is null)
        {
            return Results.NotFound(new { message = $"Coordinator does not know project '{projectId}'." });
        }

        var machine = await registry.GetMachineAsync(machineId, cancellationToken);
        if (machine is null)
        {
            return Results.NotFound(new { message = $"Coordinator does not know runner machine '{machineId}'." });
        }

        var existingWorkspace = project.Workspaces.FirstOrDefault(workspace =>
            string.Equals(workspace.MachineId, machineId, StringComparison.OrdinalIgnoreCase));
        var projectSession = projectSessions.CreateSession(
            project.Id,
            project.Name,
            machine.MachineId,
            machine.MachineName,
            companionId);
        OpenProjectOnRunnerResult? openedWorkspace = null;
        ProjectWorkspaceMapping? workspaceMapping = null;
        TerminalSession? session = null;
        try
        {
            var requestedWorkspacePath = existingWorkspace?.ProjectPath ?? BuildDefaultProjectWorkspacePath(project.Id);
            logger.LogInformation(
                "Opening project {ProjectId} ({ProjectName}) on machine {MachineId} ({MachineName}); existing workspace: {ExistingWorkspacePath}; companion: {CompanionId}; session: {ProjectSessionId}",
                project.Id,
                project.Name,
                machine.MachineId,
                machine.MachineName,
                existingWorkspace?.ProjectPath ?? "<none>",
                companionId ?? "<none>",
                projectSession.Id);

            openedWorkspace = await runners.OpenProjectAsync(
                machineId,
                new OpenProjectOnRunnerRequest
                {
                    ProjectId = project.Id,
                    ProjectName = project.Name,
                    Repository = project.Repository,
                    ExistingWorkspacePath = requestedWorkspacePath
                },
                GetActorId(httpContext),
                cancellationToken);

            if (openedWorkspace is null)
            {
                logger.LogWarning(
                    "Runner returned no workspace while opening project {ProjectId} on machine {MachineId}",
                    project.Id,
                    machine.MachineId);
                projectSessions.RemoveSession(projectSession.Id);
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(openedWorkspace.ProjectPath))
            {
                logger.LogWarning(
                    "Runner machine {MachineName} ({MachineId}) returned an empty workspace path while opening project {ProjectId}; defaulting to workspace key {WorkspaceKey}",
                    machine.MachineName,
                    machine.MachineId,
                    project.Id,
                    requestedWorkspacePath);
                openedWorkspace = BuildFallbackOpenProjectResult(machine, project, requestedWorkspacePath, existingWorkspace is not null);
            }

            logger.LogInformation(
                "Runner prepared project open for {ProjectId} on machine {MachineId} at {ProjectPath} (bootstrap pending: {BootstrapPending}, created: {WorkspaceCreated}, cloned: {RepositoryCloned})",
                project.Id,
                machine.MachineId,
                openedWorkspace.ProjectPath,
                openedWorkspace.BootstrapPending,
                openedWorkspace.WorkspaceCreated,
                openedWorkspace.RepositoryCloned);

            workspaceMapping = new ProjectWorkspaceMapping
            {
                MachineId = machine.MachineId,
                MachineName = machine.MachineName,
                ProjectPath = openedWorkspace.ProjectPath,
                IsPrimary = existingWorkspace?.IsPrimary ?? false
            };

            session = await runners.CreateSessionAsync(machineId, new CreateTerminalRequest
            {
                Name = $"{project.Name} ({machine.MachineName})",
                WorkingDirectory = string.IsNullOrWhiteSpace(openedWorkspace.TerminalWorkingDirectory)
                    ? openedWorkspace.ProjectPath
                    : openedWorkspace.TerminalWorkingDirectory,
                Command = openedWorkspace.TerminalCommand,
                Arguments = openedWorkspace.TerminalArguments
            }, cancellationToken);

            if (openedWorkspace.BootstrapPending &&
                string.IsNullOrWhiteSpace(openedWorkspace.TerminalCommand) &&
                openedWorkspace.TerminalArguments.Count == 0)
            {
                await runners.SendInputAsync(
                    session.Id,
                    BuildFallbackBootstrapInput(machine, project, openedWorkspace.ProjectPath),
                    cancellationToken);
            }

            logger.LogInformation(
                "Created project terminal session {TerminalSessionId} for project {ProjectId} on machine {MachineId} in {WorkingDirectory}",
                session.Id,
                project.Id,
                machine.MachineId,
                openedWorkspace.ProjectPath);

            if (!string.IsNullOrWhiteSpace(companionId))
            {
                companions.AttachSession(companionId, session.Id);
            }

            projectSession = projectSessions.RegisterSurface(projectSession.Id, new RegisterProjectSessionSurfaceRequest
            {
                Kind = ProjectSessionSurfaceKind.Terminal,
                DisplayName = session.Name,
                MachineId = machine.MachineId,
                MachineName = machine.MachineName,
                ReferenceId = session.Id,
                Status = openedWorkspace.BootstrapPending ? ProjectSessionSurfaceStatus.Requested : ProjectSessionSurfaceStatus.Ready,
                StatusMessage = openedWorkspace.BootstrapPending
                    ? openedWorkspace.BootstrapMessage ?? $"Terminal created and bootstrapping '{openedWorkspace.ProjectPath}'."
                    : $"Terminal ready in '{openedWorkspace.ProjectPath}'."
            });
            var updatedProject = projects.UpsertWorkspace(projectId, workspaceMapping);

            return Results.Ok(new OpenProjectOnMachineResult
            {
                Project = updatedProject,
                ProjectSession = projectSession,
                Workspace = workspaceMapping,
                Session = session,
                BootstrapPending = openedWorkspace.BootstrapPending,
                BootstrapMessage = openedWorkspace.BootstrapMessage,
                WorkspaceCreated = openedWorkspace.WorkspaceCreated,
                RepositoryCloned = openedWorkspace.RepositoryCloned
            });
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Open-project flow failed for project {ProjectId} on machine {MachineId}; workspacePath: {ProjectPath}; terminalSession: {TerminalSessionId}; projectSession: {ProjectSessionId}",
                project.Id,
                machine.MachineId,
                openedWorkspace?.ProjectPath ?? workspaceMapping?.ProjectPath ?? "<unknown>",
                session?.Id ?? "<none>",
                projectSession.Id);
            try
            {
                projectSessions.RemoveSession(projectSession.Id);
            }
            catch (Exception cleanupEx)
            {
                logger.LogWarning(cleanupEx, "Failed to remove project session during open-project cleanup");
            }

            if (workspaceMapping is not null)
            {
                try
                {
                    projects.UpsertWorkspace(projectId, workspaceMapping);
                }
                catch (Exception cleanupEx)
                {
                    logger.LogWarning(cleanupEx, "Failed to persist workspace mapping for project {ProjectId} during open-project cleanup", projectId);
                }
            }

            if (!string.IsNullOrWhiteSpace(companionId) && session is not null)
            {
                try
                {
                    companions.DetachSession(companionId, session.Id);
                }
                catch (Exception cleanupEx)
                {
                    logger.LogWarning(cleanupEx, "Failed to detach companion {CompanionId} from session {SessionId} during open-project cleanup", companionId, session.Id);
                }
            }

            try
            {
                if (session is not null)
                {
                    await runners.CloseSessionAsync(session.Id, cancellationToken);
                }
            }
            catch (Exception cleanupEx)
            {
                logger.LogWarning(cleanupEx, "Failed to close terminal session during open-project cleanup");
            }

            ExceptionDispatchInfo.Capture(ex).Throw();
            throw;
        }
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

app.Run();

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

static OpenProjectOnRunnerResult BuildFallbackOpenProjectResult(
    RegisteredRunnerMachine machine,
    ProjectDefinition project,
    string workspacePath,
    bool workspaceAlreadyMapped)
{
    var bootstrapPending = !workspaceAlreadyMapped;
    return new OpenProjectOnRunnerResult
    {
        ProjectPath = workspacePath,
        TerminalWorkingDirectory = bootstrapPending ? "." : workspacePath,
        BootstrapPending = bootstrapPending,
        BootstrapMessage = bootstrapPending
            ? $"Opened a project terminal and defaulted the workspace path to '{workspacePath}'. Bootstrap will continue in that terminal."
            : $"Opened existing workspace '{workspacePath}'."
    };
}

static string BuildDefaultProjectWorkspacePath(string projectId)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
    var normalized = projectId.Trim().ToLowerInvariant();
    var invalidCharacters = Path.GetInvalidFileNameChars();
    var builder = new StringBuilder(normalized.Length);
    foreach (var character in normalized)
    {
        if (character == Path.DirectorySeparatorChar ||
            character == Path.AltDirectorySeparatorChar)
        {
            builder.Append('-');
            continue;
        }

        builder.Append(invalidCharacters.Contains(character) ? '-' : character);
    }

    var sanitized = builder.ToString().Trim();
    if (string.IsNullOrWhiteSpace(sanitized) || sanitized.Contains("..", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Project id '{projectId}' cannot be used as a workspace path.");
    }

    return sanitized;
}

static string BuildFallbackBootstrapInput(RegisteredRunnerMachine machine, ProjectDefinition project, string workspacePath)
{
    return machine.Platform?.HostPlatform == RunnerHostPlatform.Windows
        ? BuildWindowsFallbackBootstrapInput(project, workspacePath)
        : BuildPosixFallbackBootstrapInput(project, workspacePath);
}

static string BuildWindowsFallbackBootstrapInput(ProjectDefinition project, string workspacePath)
{
    var script = new StringBuilder();
    script.Append("$AgentDeckGitPromptBackup = $env:GIT_TERMINAL_PROMPT; ");
    script.Append("$env:GIT_TERMINAL_PROMPT = '0'; ");

    if (string.IsNullOrWhiteSpace(project.Repository.Url))
    {
        script.Append($"New-Item -ItemType Directory -Force -Path {QuotePowerShell(workspacePath)} | Out-Null; ");
        script.Append($"Set-Location -LiteralPath {QuotePowerShell(workspacePath)}; ");
    }
    else
    {
        var repositoryHost = GetRepositoryHostOrDefault(project.Repository.Url);
        script.Append("if (Get-Command gh -ErrorAction SilentlyContinue) { ");
        script.Append($"  & gh auth status --active --hostname {QuotePowerShell(repositoryHost)} *> $null; ");
        script.Append("  if ($LASTEXITCODE -eq 0) { ");
        script.Append($"    & gh repo clone {QuotePowerShell(project.Repository.Url)} {QuotePowerShell(workspacePath)}");
        if (!string.IsNullOrWhiteSpace(project.Repository.DefaultBranch))
        {
            script.Append($" -- --branch {QuotePowerShell(project.Repository.DefaultBranch)}");
        }

        script.Append("; ");
        script.Append("  } else { ");
        script.Append("    Write-Host 'GitHub CLI is not authenticated in this terminal context. Falling back to git clone.'; ");
        script.Append($"    & git clone{BuildWindowsBranchArgument(project.Repository.DefaultBranch)} {QuotePowerShell(project.Repository.Url)} {QuotePowerShell(workspacePath)}; ");
        script.Append("  } ");
        script.Append("} else { ");
        script.Append($"  & git clone{BuildWindowsBranchArgument(project.Repository.DefaultBranch)} {QuotePowerShell(project.Repository.Url)} {QuotePowerShell(workspacePath)}; ");
        script.Append("} ");
        script.Append("if ($LASTEXITCODE -ne 0) { Write-Warning 'Project bootstrap did not complete automatically. Authenticate in this terminal and rerun the command if needed.'; } ");
        script.Append($"if (Test-Path -LiteralPath {QuotePowerShell(workspacePath)}) {{ Set-Location -LiteralPath {QuotePowerShell(workspacePath)}; }} ");
    }

    script.Append("if ($null -eq $AgentDeckGitPromptBackup) { Remove-Item Env:GIT_TERMINAL_PROMPT -ErrorAction SilentlyContinue; } else { $env:GIT_TERMINAL_PROMPT = $AgentDeckGitPromptBackup; }");
    script.Append("\r\n");
    return script.ToString();
}

static string BuildPosixFallbackBootstrapInput(ProjectDefinition project, string workspacePath)
{
    var script = new StringBuilder();
    script.Append("AGENTDECK_GIT_TERMINAL_PROMPT_BACKUP=${GIT_TERMINAL_PROMPT-__AGENTDECK_UNSET__}; ");
    script.Append("export GIT_TERMINAL_PROMPT=0; ");

    if (string.IsNullOrWhiteSpace(project.Repository.Url))
    {
        script.Append($"mkdir -p {QuotePosix(workspacePath)} && cd {QuotePosix(workspacePath)}; ");
    }
    else
    {
        var repositoryHost = GetRepositoryHostOrDefault(project.Repository.Url);
        script.Append("if command -v gh >/dev/null 2>&1 && ");
        script.Append($"gh auth status --active --hostname {QuotePosix(repositoryHost)} >/dev/null 2>&1; then ");
        script.Append($"gh repo clone {QuotePosix(project.Repository.Url)} {QuotePosix(workspacePath)}");
        if (!string.IsNullOrWhiteSpace(project.Repository.DefaultBranch))
        {
            script.Append($" -- --branch {QuotePosix(project.Repository.DefaultBranch)}");
        }

        script.Append("; ");
        script.Append("else ");
        script.Append("echo 'GitHub CLI is not authenticated in this terminal context. Falling back to git clone.'; ");
        script.Append($"git clone{BuildPosixBranchArgument(project.Repository.DefaultBranch)} {QuotePosix(project.Repository.Url)} {QuotePosix(workspacePath)}; ");
        script.Append("fi; ");
        script.Append("if [ $? -ne 0 ]; then echo 'Project bootstrap did not complete automatically. Authenticate in this terminal and rerun the command if needed.'; fi; ");
        script.Append($"if [ -d {QuotePosix(workspacePath)} ]; then cd {QuotePosix(workspacePath)} || true; fi; ");
    }

    script.Append("if [ \"$AGENTDECK_GIT_TERMINAL_PROMPT_BACKUP\" = \"__AGENTDECK_UNSET__\" ]; then unset GIT_TERMINAL_PROMPT; else export GIT_TERMINAL_PROMPT=\"$AGENTDECK_GIT_TERMINAL_PROMPT_BACKUP\"; fi");
    script.Append('\n');
    return script.ToString();
}

static string BuildWindowsBranchArgument(string branch) =>
    string.IsNullOrWhiteSpace(branch)
        ? string.Empty
        : $" --branch {QuotePowerShell(branch)}";

static string BuildPosixBranchArgument(string branch) =>
    string.IsNullOrWhiteSpace(branch)
        ? string.Empty
        : $" --branch {QuotePosix(branch)}";

static string GetRepositoryHostOrDefault(string? repositoryUrl) =>
    Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var repositoryUri) && !string.IsNullOrWhiteSpace(repositoryUri.Host)
        ? repositoryUri.Host
        : "github.com";

static string QuotePowerShell(string value) =>
    $"'{value.Replace("'", "''")}'";

static string QuotePosix(string value) =>
    $"'{value.Replace("'", "'\"'\"'")}'";

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
