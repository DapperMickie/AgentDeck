using AgentDeck.Runner.Configuration;
using AgentDeck.Runner.Hubs;
using AgentDeck.Runner.Services;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

if (args.Length >= 2 && string.Equals(args[0], RunnerUpdateApplyWorker.HelperModeSwitch, StringComparison.OrdinalIgnoreCase))
{
    Environment.ExitCode = await RunnerUpdateApplyWorker.RunAsync(args[1]);
    return;
}

var builder = WebApplication.CreateBuilder(args);

var runnerOptions = builder.Configuration
    .GetSection(RunnerOptions.SectionName)
    .Get<RunnerOptions>() ?? new RunnerOptions();

RunnerOptions.ApplyEnvironmentOverrides(runnerOptions);

builder.Services.AddOptions<RunnerOptions>()
    .Bind(builder.Configuration.GetSection(RunnerOptions.SectionName))
    .Configure(RunnerOptions.ApplyEnvironmentOverrides);
builder.Services.AddOptions<DesktopViewerTransportOptions>()
    .Bind(builder.Configuration.GetSection(DesktopViewerTransportOptions.SectionName));
builder.Services.AddOptions<WorkerCoordinatorOptions>()
    .Bind(builder.Configuration.GetSection(WorkerCoordinatorOptions.SectionName));
builder.Services.AddOptions<TrustPolicyOptions>()
    .Bind(builder.Configuration.GetSection(TrustPolicyOptions.SectionName));

builder.WebHost.UseUrls($"http://0.0.0.0:{runnerOptions.Port}");

builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy =>
{
    if (runnerOptions.AllowedOrigins is ["*"])
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    else
        policy.WithOrigins(runnerOptions.AllowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
}));

builder.Services.AddSignalR(opts =>
{
    opts.EnableDetailedErrors = builder.Environment.IsDevelopment();
    opts.MaximumReceiveMessageSize = 1024 * 1024;
});

builder.Services.AddHttpClient();
builder.Services.AddSingleton<IAgentSessionStore, AgentSessionStore>();
builder.Services.AddSingleton<IOrchestrationJobService, OrchestrationJobService>();
builder.Services.AddSingleton<IOrchestrationExecutionService, OrchestrationExecutionService>();
builder.Services.AddSingleton<IRemoteViewerSessionService, RemoteViewerSessionService>();
builder.Services.AddSingleton<IRunnerConnectionUrlResolver, RunnerConnectionUrlResolver>();
builder.Services.AddSingleton<CoordinatorRunnerConnectionService>();
builder.Services.AddSingleton<IManagedViewerRelayService, ManagedViewerRelayService>();
builder.Services.AddSingleton<IDesktopViewerBootstrapService, DesktopViewerBootstrapService>();
builder.Services.AddSingleton<IRunnerAuditService, RunnerAuditService>();
builder.Services.AddSingleton<IRunnerTrustPolicy, RunnerTrustPolicy>();
builder.Services.AddSingleton<IRunnerUpdateStagingService, RunnerUpdateStagingService>();
builder.Services.AddSingleton<IRunnerWorkflowCatalogService, RunnerWorkflowCatalogService>();
builder.Services.AddSingleton<IRunnerCapabilityCatalogService, RunnerCapabilityCatalogService>();
builder.Services.AddSingleton<IRunnerSetupCatalogService, RunnerSetupCatalogService>();
builder.Services.AddSingleton<IRunnerWorkflowPackService, RunnerWorkflowPackService>();
builder.Services.AddSingleton<IVsCodeDebugSessionService, VsCodeDebugSessionService>();
builder.Services.AddSingleton<IVirtualDeviceCatalogService, VirtualDeviceCatalogService>();
builder.Services.AddSingleton<IWorkspaceService, WorkspaceService>();
builder.Services.AddSingleton<ITerminalSessionService, TerminalSessionService>();
builder.Services.AddSingleton<IProjectWorkspaceBootstrapService, ProjectWorkspaceBootstrapService>();
builder.Services.AddSingleton<IMachineCapabilityService, MachineCapabilityService>();
builder.Services.AddSingleton<IMachineSetupService, MachineSetupService>();
builder.Services.AddSingleton<IPtyProcessManager, PtyProcessManager>();
builder.Services.AddHostedService<HubOutputForwarder>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CoordinatorRunnerConnectionService>());
builder.Services.AddHostedService(sp => (OrchestrationExecutionService)sp.GetRequiredService<IOrchestrationExecutionService>());
builder.Services.AddHostedService<WorkerCoordinatorRegistrationService>();

var app = builder.Build();

app.UseCors();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));

app.MapGet("/api/audit/events", (IRunnerAuditService audit) =>
    Results.Ok(audit.GetRecent()));

app.MapGet("/api/sessions", (IAgentSessionStore store) =>
    Results.Ok(store.GetAll()));

app.MapPost("/api/sessions", async (CreateTerminalRequest request, ITerminalSessionService terminalSessions, Microsoft.AspNetCore.SignalR.IHubContext<AgentDeck.Runner.Hubs.AgentHub, AgentDeck.Shared.Hubs.IAgentHubClient> hubContext, CancellationToken cancellationToken) =>
{
    try
    {
        var session = await terminalSessions.CreateSessionAsync(request, cancellationToken);
        await hubContext.Clients.All.SessionCreatedAsync(session);
        return Results.Ok(session);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (TerminalSessionStartException ex)
    {
        await hubContext.Clients.All.SessionCreatedAsync(ex.Session);
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapDelete("/api/sessions/{id}", async (string id, IAgentSessionStore store, IPtyProcessManager ptyManager) =>
{
    if (store.Get(id) is null) return Results.NotFound();
    await ptyManager.KillAsync(id);
    store.Remove(id);
    return Results.NoContent();
});

app.MapGet("/api/orchestration/jobs", (IOrchestrationJobService jobs) =>
    Results.Ok(jobs.GetAll()));

app.MapGet("/api/orchestration/jobs/{id}", (string id, IOrchestrationJobService jobs) =>
    jobs.Get(id) is { } job ? Results.Ok(job) : Results.NotFound());

app.MapPost("/api/orchestration/jobs", (CreateOrchestrationJobRequest request, HttpContext httpContext, IOrchestrationJobService jobs, IOrchestrationExecutionService execution, IRunnerTrustPolicy trustPolicy, IRunnerAuditService audit) =>
{
    var decision = trustPolicy.Evaluate(
        httpContext,
        action: "orchestration.queue",
        targetType: "project",
        targetId: request.ProjectId,
        targetDisplayName: request.ProjectName);

    if (!decision.Allowed)
    {
        return Deny(decision, audit);
    }

    if (request.DeviceSelection is not null && !request.DeviceSelection.HasTarget)
    {
        audit.Record(decision, RunnerAuditOutcome.Failed, "Device selection must include either a device ID or a profile ID.");
        return Results.BadRequest(new
        {
            message = "Device selection must include either a device ID or a profile ID."
        });
    }

    var job = jobs.Queue(request);
    execution.Start(job.Id);
    audit.Record(decision, RunnerAuditOutcome.Succeeded, $"Queued orchestration job '{job.Id}' for launch profile '{request.LaunchProfileId}'.");
    return Results.Ok(job);
});

app.MapPost("/api/orchestration/jobs/{id}/status", (string id, UpdateOrchestrationJobStatusRequest request, IOrchestrationJobService jobs) =>
    jobs.UpdateStatus(id, request) is { } job ? Results.Ok(job) : Results.NotFound());

app.MapPost("/api/orchestration/jobs/{id}/logs", (string id, AppendOrchestrationJobLogRequest request, IOrchestrationJobService jobs) =>
    jobs.AppendLog(id, request) is { } job ? Results.Ok(job) : Results.NotFound());

app.MapPost("/api/orchestration/jobs/{id}/cancel", async (string id, HttpContext httpContext, IOrchestrationJobService jobs, IOrchestrationExecutionService execution, IRunnerTrustPolicy trustPolicy, IRunnerAuditService audit, CancellationToken cancellationToken) =>
{
    var decision = trustPolicy.Evaluate(
        httpContext,
        action: "orchestration.cancel",
        targetType: "orchestration-job",
        targetId: id);

    if (!decision.Allowed)
    {
        return Deny(decision, audit);
    }

    if (jobs.RequestCancellation(id) is null)
    {
        audit.Record(decision, RunnerAuditOutcome.Failed, "The orchestration job was not found.");
        return Results.NotFound();
    }

    await execution.RequestCancellationAsync(id, cancellationToken);
    audit.Record(decision, RunnerAuditOutcome.Succeeded, $"Requested cancellation for orchestration job '{id}'.");
    return jobs.Get(id) is { } job ? Results.Ok(job) : Results.NotFound();
});

app.MapGet("/api/viewers/providers", (IRemoteViewerSessionService viewers) =>
    Results.Ok(viewers.GetAvailableProviders()));

app.MapGet("/api/debug/vscode/sessions", (IVsCodeDebugSessionService debugSessions) =>
    Results.Ok(debugSessions.GetAll()));

app.MapGet("/api/debug/vscode/sessions/{id}", (string id, IVsCodeDebugSessionService debugSessions) =>
    debugSessions.Get(id) is { } session ? Results.Ok(session) : Results.NotFound());

app.MapGet("/api/viewers/sessions", (IRemoteViewerSessionService viewers) =>
    Results.Ok(viewers.GetAll()));

app.MapGet("/api/viewers/sessions/{id}", (string id, IRemoteViewerSessionService viewers) =>
    viewers.Get(id) is { } session ? Results.Ok(session) : Results.NotFound());

app.MapPost("/api/viewers/sessions", async (CreateRemoteViewerSessionRequest request, HttpContext httpContext, IRemoteViewerSessionService viewers, IDesktopViewerBootstrapService desktopBootstrap, IRunnerTrustPolicy trustPolicy, IRunnerAuditService audit, CancellationToken cancellationToken) =>
{
    var action = request.Target.Kind == RemoteViewerTargetKind.Desktop
        ? "viewer.desktop.create"
        : "viewer.create";
    var decision = trustPolicy.Evaluate(
        httpContext,
        action,
        targetType: request.Target.Kind.ToString(),
        targetId: request.JobId ?? request.Target.JobId,
        targetDisplayName: request.Target.DisplayName);

    if (!decision.Allowed)
    {
        return Deny(decision, audit);
    }

    var session = viewers.Create(request);
    session = await desktopBootstrap.BootstrapAsync(
        session.Id,
        httpContext.Request.Host.Host,
        $"{httpContext.Request.Scheme}://{httpContext.Request.Host.Value}",
        cancellationToken) ?? session;

    audit.Record(
        decision,
        session.Status == RemoteViewerSessionStatus.Failed ? RunnerAuditOutcome.Failed : RunnerAuditOutcome.Succeeded,
        session.StatusMessage ?? $"Created viewer session '{session.Id}'.");

    return Results.Ok(session);
});

app.MapPost("/api/viewers/sessions/{id}/status", async (string id, UpdateRemoteViewerSessionRequest request, HttpContext httpContext, IRemoteViewerSessionService viewers, IDesktopViewerBootstrapService desktopBootstrap, IRunnerTrustPolicy trustPolicy, IRunnerAuditService audit, CancellationToken cancellationToken) =>
{
    if (request.Status == RemoteViewerSessionStatus.Closed)
    {
        var decision = trustPolicy.Evaluate(httpContext, "viewer.close", "viewer-session", id);
        if (!decision.Allowed)
        {
            return Deny(decision, audit);
        }

        var closeResult = await desktopBootstrap.CloseAsync(id, request.Message, cancellationToken);
        if (closeResult.Session is not null)
        {
            audit.Record(
                decision,
                closeResult.Outcome == RemoteViewerSessionMutationOutcome.Updated ? RunnerAuditOutcome.Succeeded : RunnerAuditOutcome.Failed,
                closeResult.Session.StatusMessage);
        }
        return closeResult.Outcome switch
        {
            RemoteViewerSessionMutationOutcome.Updated when closeResult.Session is not null => Results.Ok(closeResult.Session),
            RemoteViewerSessionMutationOutcome.InvalidTransition when closeResult.Session is not null => Results.Conflict(closeResult.Session),
            _ => Results.NotFound()
        };
    }

    var result = viewers.Update(id, request);
    return result.Outcome switch
    {
        RemoteViewerSessionMutationOutcome.Updated when result.Session is not null => Results.Ok(result.Session),
        RemoteViewerSessionMutationOutcome.InvalidTransition when result.Session is not null => Results.Conflict(result.Session),
        _ => Results.NotFound()
    };
});

app.MapPost("/api/viewers/sessions/{id}/close", async (string id, HttpContext httpContext, IDesktopViewerBootstrapService desktopBootstrap, IRunnerTrustPolicy trustPolicy, IRunnerAuditService audit, CancellationToken cancellationToken) =>
{
    var decision = trustPolicy.Evaluate(httpContext, "viewer.close", "viewer-session", id);
    if (!decision.Allowed)
    {
        return Deny(decision, audit);
    }

    var result = await desktopBootstrap.CloseAsync(id, cancellationToken: cancellationToken);
    if (result.Session is not null)
    {
        audit.Record(
            decision,
            result.Outcome == RemoteViewerSessionMutationOutcome.Updated ? RunnerAuditOutcome.Succeeded : RunnerAuditOutcome.Failed,
            result.Session.StatusMessage);
    }
    return result.Outcome switch
    {
        RemoteViewerSessionMutationOutcome.Updated when result.Session is not null => Results.Ok(result.Session),
        RemoteViewerSessionMutationOutcome.InvalidTransition when result.Session is not null => Results.Conflict(result.Session),
        _ => Results.NotFound()
    };
});

app.MapGet("/api/virtual-devices/catalogs", async (IVirtualDeviceCatalogService devices, CancellationToken cancellationToken) =>
    Results.Ok(await devices.GetCatalogsAsync(cancellationToken)));

app.MapGet("/api/virtual-devices/catalogs/{catalogKind}", async (string catalogKind, IVirtualDeviceCatalogService devices, CancellationToken cancellationToken) =>
{
    if (!Enum.TryParse<VirtualDeviceCatalogKind>(catalogKind, true, out var parsedCatalogKind))
    {
        return Results.BadRequest(new
        {
            message = $"Unknown virtual device catalog '{catalogKind}'."
        });
    }

    return await devices.GetCatalogAsync(parsedCatalogKind, cancellationToken) is { } snapshot ? Results.Ok(snapshot) : Results.NotFound();
});

app.MapPost("/api/virtual-devices/resolve", async (VirtualDeviceLaunchSelection selection, IVirtualDeviceCatalogService devices, CancellationToken cancellationToken) =>
{
    if (!selection.HasTarget)
    {
        return Results.BadRequest(new
        {
            message = "Device selection must include either a device ID or a profile ID."
        });
    }

    return Results.Ok(await devices.ResolveSelectionAsync(selection, cancellationToken));
});

app.MapGet("/api/workspace", (IWorkspaceService workspace) =>
    Results.Ok(workspace.GetWorkspaceInfo()));

app.MapPost("/api/projects/open", async (OpenProjectOnRunnerRequest request, IProjectWorkspaceBootstrapService bootstrap, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await bootstrap.OpenProjectAsync(request, cancellationToken));
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

app.MapGet("/api/capabilities", async (IMachineCapabilityService capabilities, CancellationToken cancellationToken) =>
    Results.Ok(await capabilities.GetSnapshotAsync(cancellationToken)));

app.MapPost("/api/capabilities/{capabilityId}/install", async (string capabilityId, MachineCapabilityInstallRequest? request, HttpContext httpContext, IMachineSetupService setup, IRunnerTrustPolicy trustPolicy, IRunnerAuditService audit, CancellationToken cancellationToken) =>
{
    var decision = trustPolicy.Evaluate(httpContext, "capability.install", "capability", capabilityId, capabilityId);
    if (!decision.Allowed)
    {
        return Deny(decision, audit);
    }

    var result = await setup.InstallCapabilityAsync(capabilityId, request?.Version, cancellationToken);
    audit.Record(decision, result.Succeeded ? RunnerAuditOutcome.Succeeded : RunnerAuditOutcome.Failed, result.Message);
    return Results.Ok(result);
});

app.MapPost("/api/capabilities/{capabilityId}/update", async (string capabilityId, HttpContext httpContext, IMachineSetupService setup, IRunnerTrustPolicy trustPolicy, IRunnerAuditService audit, CancellationToken cancellationToken) =>
{
    var decision = trustPolicy.Evaluate(httpContext, "capability.update", "capability", capabilityId, capabilityId);
    if (!decision.Allowed)
    {
        return Deny(decision, audit);
    }

    var result = await setup.UpdateCapabilityAsync(capabilityId, cancellationToken);
    audit.Record(decision, result.Succeeded ? RunnerAuditOutcome.Succeeded : RunnerAuditOutcome.Failed, result.Message);
    return Results.Ok(result);
});

app.MapPost("/api/workflow-packs/current/retry", async (HttpContext httpContext, IRunnerWorkflowPackService workflowPacks, IRunnerTrustPolicy trustPolicy, IRunnerAuditService audit, CancellationToken cancellationToken) =>
{
    var currentStatus = await workflowPacks.GetCurrentStatusAsync(cancellationToken);
    var targetId = currentStatus?.PackId ?? "current";
    var targetDisplayName = currentStatus?.PackVersion is { Length: > 0 }
        ? $"{targetId}@{currentStatus.PackVersion}"
        : targetId;
    var decision = trustPolicy.Evaluate(httpContext, "workflow-pack.retry", "workflow-pack", targetId, targetDisplayName);
    if (!decision.Allowed)
    {
        return Deny(decision, audit);
    }

    await workflowPacks.ResetCurrentStatusAsync(cancellationToken);
    audit.Record(decision, RunnerAuditOutcome.Succeeded, $"Cleared workflow pack state for {targetDisplayName}.");
    return Results.NoContent();
});

app.MapHub<AgentHub>("/hubs/agent");
app.MapHub<ManagedViewerRelayHub>("/hubs/managed-viewer");

app.Lifetime.ApplicationStarted.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("AgentDeck Runner started — listening on port {Port}", runnerOptions.Port);
    logger.LogInformation("Workspace root: {Root}",
        app.Services.GetRequiredService<IWorkspaceService>().GetWorkspaceRoot());
});

app.Run();

static IResult Deny(RunnerTrustDecision decision, IRunnerAuditService audit)
{
    audit.Record(decision, RunnerAuditOutcome.Denied, decision.Message);
    return Results.Json(
        new
        {
            message = decision.Message ?? "This action is denied by the current trust policy."
        },
        statusCode: StatusCodes.Status403Forbidden);
}
