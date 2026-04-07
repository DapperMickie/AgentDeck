using AgentDeck.Runner.Configuration;
using AgentDeck.Runner.Hubs;
using AgentDeck.Runner.Services;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

var builder = WebApplication.CreateBuilder(args);

var runnerOptions = builder.Configuration
    .GetSection(RunnerOptions.SectionName)
    .Get<RunnerOptions>() ?? new RunnerOptions();

RunnerOptions.ApplyEnvironmentOverrides(runnerOptions);

builder.Services.AddOptions<RunnerOptions>()
    .Bind(builder.Configuration.GetSection(RunnerOptions.SectionName))
    .Configure(RunnerOptions.ApplyEnvironmentOverrides);

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

builder.Services.AddSingleton<IAgentSessionStore, AgentSessionStore>();
builder.Services.AddSingleton<IOrchestrationJobService, OrchestrationJobService>();
builder.Services.AddSingleton<IOrchestrationExecutionService, OrchestrationExecutionService>();
builder.Services.AddSingleton<IRemoteViewerSessionService, RemoteViewerSessionService>();
builder.Services.AddSingleton<IVirtualDeviceCatalogService, VirtualDeviceCatalogService>();
builder.Services.AddSingleton<IWorkspaceService, WorkspaceService>();
builder.Services.AddSingleton<IMachineCapabilityService, MachineCapabilityService>();
builder.Services.AddSingleton<IMachineSetupService, MachineSetupService>();
builder.Services.AddSingleton<IPtyProcessManager, PtyProcessManager>();
builder.Services.AddHostedService<HubOutputForwarder>();
builder.Services.AddHostedService(sp => (OrchestrationExecutionService)sp.GetRequiredService<IOrchestrationExecutionService>());

var app = builder.Build();

app.UseCors();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));

app.MapGet("/api/sessions", (IAgentSessionStore store) =>
    Results.Ok(store.GetAll()));

app.MapPost("/api/sessions", () => Results.StatusCode(501));

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

app.MapPost("/api/orchestration/jobs", (CreateOrchestrationJobRequest request, IOrchestrationJobService jobs, IOrchestrationExecutionService execution) =>
{
    if (request.DeviceSelection is not null && !request.DeviceSelection.HasTarget)
    {
        return Results.BadRequest(new
        {
            message = "Device selection must include either a device ID or a profile ID."
        });
    }

    var job = jobs.Queue(request);
    execution.Start(job.Id);
    return Results.Ok(job);
});

app.MapPost("/api/orchestration/jobs/{id}/status", (string id, UpdateOrchestrationJobStatusRequest request, IOrchestrationJobService jobs) =>
    jobs.UpdateStatus(id, request) is { } job ? Results.Ok(job) : Results.NotFound());

app.MapPost("/api/orchestration/jobs/{id}/logs", (string id, AppendOrchestrationJobLogRequest request, IOrchestrationJobService jobs) =>
    jobs.AppendLog(id, request) is { } job ? Results.Ok(job) : Results.NotFound());

app.MapPost("/api/orchestration/jobs/{id}/cancel", async (string id, IOrchestrationJobService jobs, IOrchestrationExecutionService execution, CancellationToken cancellationToken) =>
{
    if (jobs.RequestCancellation(id) is null)
    {
        return Results.NotFound();
    }

    await execution.RequestCancellationAsync(id, cancellationToken);
    return jobs.Get(id) is { } job ? Results.Ok(job) : Results.NotFound();
});

app.MapGet("/api/viewers/providers", (IRemoteViewerSessionService viewers) =>
    Results.Ok(viewers.GetAvailableProviders()));

app.MapGet("/api/viewers/sessions", (IRemoteViewerSessionService viewers) =>
    Results.Ok(viewers.GetAll()));

app.MapGet("/api/viewers/sessions/{id}", (string id, IRemoteViewerSessionService viewers) =>
    viewers.Get(id) is { } session ? Results.Ok(session) : Results.NotFound());

app.MapPost("/api/viewers/sessions", (CreateRemoteViewerSessionRequest request, IRemoteViewerSessionService viewers) =>
    Results.Ok(viewers.Create(request)));

app.MapPost("/api/viewers/sessions/{id}/status", (string id, UpdateRemoteViewerSessionRequest request, IRemoteViewerSessionService viewers) =>
{
    var result = viewers.Update(id, request);
    return result.Outcome switch
    {
        RemoteViewerSessionMutationOutcome.Updated when result.Session is not null => Results.Ok(result.Session),
        RemoteViewerSessionMutationOutcome.InvalidTransition when result.Session is not null => Results.Conflict(result.Session),
        _ => Results.NotFound()
    };
});

app.MapPost("/api/viewers/sessions/{id}/close", (string id, IRemoteViewerSessionService viewers) =>
{
    var result = viewers.Close(id);
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

app.MapGet("/api/capabilities", async (IMachineCapabilityService capabilities, CancellationToken cancellationToken) =>
    Results.Ok(await capabilities.GetSnapshotAsync(cancellationToken)));

app.MapPost("/api/capabilities/{capabilityId}/install", async (string capabilityId, MachineCapabilityInstallRequest? request, IMachineSetupService setup, CancellationToken cancellationToken) =>
    Results.Ok(await setup.InstallCapabilityAsync(capabilityId, request?.Version, cancellationToken)));

app.MapPost("/api/capabilities/{capabilityId}/update", async (string capabilityId, IMachineSetupService setup, CancellationToken cancellationToken) =>
    Results.Ok(await setup.UpdateCapabilityAsync(capabilityId, cancellationToken)));

app.MapHub<AgentHub>("/hubs/agent");

app.Lifetime.ApplicationStarted.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("AgentDeck Runner started — listening on port {Port}", runnerOptions.Port);
    logger.LogInformation("Workspace root: {Root}",
        app.Services.GetRequiredService<IWorkspaceService>().GetWorkspaceRoot());
});

app.Run();
