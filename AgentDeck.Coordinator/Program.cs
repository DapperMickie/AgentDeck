using AgentDeck.Coordinator.Configuration;
using AgentDeck.Coordinator.Services;
using AgentDeck.Coordinator.Endpoints;
using AgentDeck.Shared;
using AgentDeck.Shared.Protocol;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

var coordinatorOptions = builder.Configuration
    .GetSection(CoordinatorOptions.SectionName)
    .Get<CoordinatorOptions>() ?? new CoordinatorOptions();

CoordinatorOptions.ApplyEnvironmentOverrides(coordinatorOptions);

builder.WebHost.UseUrls($"http://{coordinatorOptions.BindAddress}:{coordinatorOptions.Port}");

builder.Services.AddOptions<CoordinatorOptions>()
    .Bind(builder.Configuration.GetSection(CoordinatorOptions.SectionName))
    .Configure(CoordinatorOptions.ApplyEnvironmentOverrides);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.KeepAliveInterval = coordinatorOptions.RunnerControlKeepAliveInterval;
    options.ClientTimeoutInterval = coordinatorOptions.RunnerControlClientTimeoutInterval;
    options.HandshakeTimeout = coordinatorOptions.RunnerControlHandshakeTimeout;
    options.MaximumReceiveMessageSize = coordinatorOptions.RunnerControlMaximumReceiveMessageSize;
})
.AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddSingleton<ICoordinatorArtifactService, CoordinatorArtifactService>();
builder.Services.AddSingleton<IRunnerDefinitionCatalogService, RunnerDefinitionCatalogService>();
builder.Services.AddSingleton<IWorkerRegistryService, WorkerRegistryService>();
builder.Services.AddSingleton<ICompanionRegistryService, CompanionRegistryService>();
builder.Services.AddSingleton<IProjectRegistryService, ProjectRegistryService>();
builder.Services.AddSingleton<IProjectSessionRegistryService, ProjectSessionRegistryService>();
builder.Services.AddSingleton<IProjectOpenService, ProjectOpenService>();
builder.Services.AddSingleton<IMachineRemoteControlRegistryService, MachineRemoteControlRegistryService>();
builder.Services.AddSingleton<IRunnerOrchestrationService, RunnerOrchestrationService>();
builder.Services.AddSingleton<RunnerBrokerService>();
builder.Services.AddSingleton<IRunnerBrokerService>(static services => services.GetRequiredService<RunnerBrokerService>());

var app = builder.Build();

app.Logger.LogInformation(
    "Starting AgentDeck coordinator on port {Port} with worker heartbeat {HeartbeatInterval} and expiry {WorkerExpiry}",
    coordinatorOptions.Port,
    coordinatorOptions.WorkerHeartbeatInterval,
    coordinatorOptions.WorkerExpiry);

if (AgentDeckAccessKey.IsConfigured(coordinatorOptions.AccessKey))
{
    app.Use(async (context, next) =>
    {
        if (HttpMethods.IsOptions(context.Request.Method) ||
            (HttpMethods.IsGet(context.Request.Method) && context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        var suppliedAccessKey = context.Request.Headers[AgentDeckHeaderNames.AccessKey].FirstOrDefault()
            ?? context.Request.Query["access_key"].FirstOrDefault();
        if (!AgentDeckAccessKey.Matches(coordinatorOptions.AccessKey, suppliedAccessKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { message = "AgentDeck access key is required." });
            return;
        }

        await next(context);
    });
}

app.MapCoordinatorEndpoints();

app.Run();
