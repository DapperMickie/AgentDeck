using AgentDeck.Coordinator.Configuration;
using AgentDeck.Coordinator.Hubs;
using AgentDeck.Coordinator.Services;
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
builder.Services.AddSingleton<RunnerBrokerService>();
builder.Services.AddSingleton<IRunnerBrokerService>(static services => services.GetRequiredService<RunnerBrokerService>());

var app = builder.Build();

app.Logger.LogInformation(
    "Starting AgentDeck coordinator on port {Port} with worker heartbeat {HeartbeatInterval} and expiry {WorkerExpiry}",
    coordinatorOptions.Port,
    coordinatorOptions.WorkerHeartbeatInterval,
    coordinatorOptions.WorkerExpiry);

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));

app.MapGet("/api/machines", async (IWorkerRegistryService registry, CancellationToken cancellationToken) =>
    Results.Ok(await registry.GetMachinesAsync(cancellationToken)));

app.MapGet("/api/machines/{machineId}/workspace", async (string machineId, IRunnerBrokerService runners, CancellationToken cancellationToken) =>
{
    try
    {
        var workspace = await runners.GetWorkspaceAsync(machineId, cancellationToken);
        return workspace is null ? Results.NotFound() : Results.Ok(workspace);
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(new { message = ex.Message });
    }
});

app.MapGet("/api/machines/{machineId}/capabilities", async (string machineId, IRunnerBrokerService runners, CancellationToken cancellationToken) =>
{
    try
    {
        var capabilities = await runners.GetMachineCapabilitiesAsync(machineId, cancellationToken);
        return capabilities is null ? Results.NotFound() : Results.Ok(capabilities);
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(new { message = ex.Message });
    }
});

app.MapPost("/api/machines/{machineId}/capabilities/{capabilityId}/install", async (string machineId, string capabilityId, MachineCapabilityInstallRequest? request, HttpContext httpContext, IRunnerBrokerService runners, CancellationToken cancellationToken) =>
{
    try
    {
        var actorId = httpContext.Request.Headers["X-AgentDeck-Actor"].FirstOrDefault();
        var result = await runners.InstallMachineCapabilityAsync(machineId, capabilityId, request ?? new MachineCapabilityInstallRequest(), actorId ?? "coordinator", cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(new { message = ex.Message });
    }
});

app.MapPost("/api/machines/{machineId}/capabilities/{capabilityId}/update", async (string machineId, string capabilityId, HttpContext httpContext, IRunnerBrokerService runners, CancellationToken cancellationToken) =>
{
    try
    {
        var actorId = httpContext.Request.Headers["X-AgentDeck-Actor"].FirstOrDefault();
        var result = await runners.UpdateMachineCapabilityAsync(machineId, capabilityId, actorId ?? "coordinator", cancellationToken);
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
