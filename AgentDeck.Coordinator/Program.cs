using AgentDeck.Coordinator.Configuration;
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
builder.Services.AddSingleton<IWorkerRegistryService, WorkerRegistryService>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));

app.MapGet("/api/machines", async (IWorkerRegistryService registry, CancellationToken cancellationToken) =>
    Results.Ok(await registry.GetMachinesAsync(cancellationToken)));

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

app.Run();
