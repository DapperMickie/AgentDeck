using AgentDeck.Runner.Configuration;
using AgentDeck.Runner.Hubs;
using AgentDeck.Runner.Services;

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
builder.Services.AddSingleton<IWorkspaceService, WorkspaceService>();
builder.Services.AddSingleton<IMachineCapabilityService, MachineCapabilityService>();
builder.Services.AddSingleton<IMachineSetupService, MachineSetupService>();
builder.Services.AddSingleton<IPtyProcessManager, PtyProcessManager>();
builder.Services.AddHostedService<HubOutputForwarder>();

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

app.MapGet("/api/workspace", (IWorkspaceService workspace) =>
    Results.Ok(workspace.GetWorkspaceInfo()));

app.MapGet("/api/capabilities", async (IMachineCapabilityService capabilities, CancellationToken cancellationToken) =>
    Results.Ok(await capabilities.GetSnapshotAsync(cancellationToken)));

app.MapPost("/api/capabilities/{capabilityId}/install", async (string capabilityId, IMachineSetupService setup, CancellationToken cancellationToken) =>
    Results.Ok(await setup.InstallCapabilityAsync(capabilityId, cancellationToken)));

app.MapHub<AgentHub>("/hubs/agent");

app.Lifetime.ApplicationStarted.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("AgentDeck Runner started — listening on port {Port}", runnerOptions.Port);
    logger.LogInformation("Workspace root: {Root}",
        app.Services.GetRequiredService<IWorkspaceService>().GetWorkspaceRoot());
});

app.Run();
