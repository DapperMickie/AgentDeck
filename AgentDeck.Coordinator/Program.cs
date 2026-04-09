using AgentDeck.Coordinator.Configuration;
using AgentDeck.Coordinator.Hubs;
using AgentDeck.Coordinator.Services;
using AgentDeck.Shared;
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
builder.Services.AddSingleton<IRunnerDefinitionCatalogService, RunnerDefinitionCatalogService>();
builder.Services.AddSingleton<IWorkerRegistryService, WorkerRegistryService>();
builder.Services.AddSingleton<ICompanionRegistryService, CompanionRegistryService>();
builder.Services.AddSingleton<IProjectRegistryService, ProjectRegistryService>();
builder.Services.AddSingleton<RunnerBrokerService>();
builder.Services.AddSingleton<IRunnerBrokerService>(static services => services.GetRequiredService<RunnerBrokerService>());

var app = builder.Build();

app.Logger.LogInformation(
    "Starting AgentDeck coordinator on port {Port} with worker heartbeat {HeartbeatInterval} and expiry {WorkerExpiry}",
    coordinatorOptions.Port,
    coordinatorOptions.WorkerHeartbeatInterval,
    coordinatorOptions.WorkerExpiry);

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));

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
