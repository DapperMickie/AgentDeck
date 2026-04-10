using AgentDeck.Coordinator.Configuration;
using AgentDeck.Coordinator.Hubs;
using AgentDeck.Coordinator.Services;
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
builder.Services.AddSignalR();
builder.Services.AddSingleton<ICoordinatorArtifactService, CoordinatorArtifactService>();
builder.Services.AddSingleton<IRunnerDefinitionCatalogService, RunnerDefinitionCatalogService>();
builder.Services.AddSingleton<IWorkerRegistryService, WorkerRegistryService>();
builder.Services.AddSingleton<ICompanionRegistryService, CompanionRegistryService>();
builder.Services.AddSingleton<IProjectRegistryService, ProjectRegistryService>();
builder.Services.AddSingleton<IProjectSessionRegistryService, ProjectSessionRegistryService>();
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
        return Results.Conflict(new { message = ex.Message });
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

        if (RejectProjectMutationIfViewer(httpContext, projectSessions, projectId, machineId) is { } rejection)
        {
            return rejection;
        }

        var existingWorkspace = project.Workspaces.FirstOrDefault(workspace =>
            string.Equals(workspace.MachineId, machineId, StringComparison.OrdinalIgnoreCase));
        var openedWorkspace = await runners.OpenProjectAsync(
            machineId,
            new OpenProjectOnRunnerRequest
            {
                ProjectId = project.Id,
                ProjectName = project.Name,
                Repository = project.Repository,
                ExistingWorkspacePath = existingWorkspace?.ProjectPath
            },
            GetActorId(httpContext),
            cancellationToken);

        if (openedWorkspace is null)
        {
            return Results.NotFound();
        }

        var workspaceMapping = new ProjectWorkspaceMapping
        {
            MachineId = machine.MachineId,
            MachineName = machine.MachineName,
            ProjectPath = openedWorkspace.ProjectPath,
            IsPrimary = existingWorkspace?.IsPrimary ?? false
        };

        var session = await runners.CreateSessionAsync(machineId, new CreateTerminalRequest
        {
            Name = $"{project.Name} ({machine.MachineName})",
            WorkingDirectory = openedWorkspace.ProjectPath
        }, cancellationToken);
        if (!string.IsNullOrWhiteSpace(companionId))
        {
            companions.AttachSession(companionId, session.Id);
        }

        var projectSession = projectSessions.CreateSession(
            project.Id,
            project.Name,
            machine.MachineId,
            machine.MachineName,
            companionId);
        try
        {
            projectSession = projectSessions.RegisterSurface(projectSession.Id, new RegisterProjectSessionSurfaceRequest
            {
                Kind = ProjectSessionSurfaceKind.Terminal,
                DisplayName = session.Name,
                MachineId = machine.MachineId,
                MachineName = machine.MachineName,
                ReferenceId = session.Id,
                Status = ProjectSessionSurfaceStatus.Ready,
                StatusMessage = $"Terminal ready in '{openedWorkspace.ProjectPath}'."
            });
            var updatedProject = projects.UpsertWorkspace(projectId, workspaceMapping);

            return Results.Ok(new OpenProjectOnMachineResult
            {
                Project = updatedProject,
                ProjectSession = projectSession,
                Workspace = workspaceMapping,
                Session = session,
                WorkspaceCreated = openedWorkspace.WorkspaceCreated,
                RepositoryCloned = openedWorkspace.RepositoryCloned
            });
        }
        catch (Exception ex)
        {
            try
            {
                projectSessions.RemoveSession(projectSession.Id);
            }
            catch (Exception cleanupEx)
            {
                logger.LogWarning(cleanupEx, "Failed to remove project session {ProjectSessionId} during open-project cleanup", projectSession.Id);
            }

            if (!string.IsNullOrWhiteSpace(companionId))
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
                await runners.CloseSessionAsync(session.Id, cancellationToken);
            }
            catch (Exception cleanupEx)
            {
                logger.LogWarning(cleanupEx, "Failed to close terminal session {SessionId} during open-project cleanup", session.Id);
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
        return Results.Conflict(new { message = ex.Message });
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
        return Results.BadRequest(new { message = ex.Message });
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
        return Results.BadRequest(new { message = ex.Message });
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

app.MapGet("/api/machines/{machineId}/virtual-devices/catalogs", async (string machineId, HttpContext httpContext, ICompanionRegistryService companions, IRunnerBrokerService runners, CancellationToken cancellationToken) =>
{
    try
    {
        TrackMachineAttachment(httpContext, companions, machineId);
        return Results.Ok(await runners.GetVirtualDeviceCatalogsAsync(machineId, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(new { message = ex.Message });
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
        return Results.NotFound(new { message = ex.Message });
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

app.MapGet("/api/runner-definitions/update-manifests/{manifestId}", (string manifestId, IRunnerDefinitionCatalogService catalog) =>
    catalog.GetUpdateManifest(manifestId) is { } manifest
        ? Results.Ok(manifest)
        : Results.NotFound());

app.MapGet("/api/runner-definitions/workflow-packs/{packId}", (string packId, IRunnerDefinitionCatalogService catalog) =>
    catalog.GetWorkflowPack(packId) is { } pack
        ? Results.Ok(pack)
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
    string machineId) =>
    projectSessions.GetSessions(projectId)
        .Where(session =>
            string.Equals(session.ProjectId, projectId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(session.MachineId, machineId, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(session => session.UpdatedAt)
        .FirstOrDefault();
