using AgentDeck.Runner.Configuration;
using AgentDeck.Runner.Services;
using AgentDeck.Runner.Endpoints;
using AgentDeck.Shared.Protocol;
using System.Text.Json.Serialization;

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
})
.AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IAgentSessionStore, AgentSessionStore>();
builder.Services.AddSingleton<CoordinatorRunnerConnectionState>();
builder.Services.AddSingleton<IOrchestrationJobService, OrchestrationJobService>();
builder.Services.AddSingleton<IOrchestrationExecutionService, OrchestrationExecutionService>();
builder.Services.AddSingleton<IRemoteViewerSessionService, RemoteViewerSessionService>();
builder.Services.AddSingleton<IRunnerConnectionUrlResolver, RunnerConnectionUrlResolver>();
builder.Services.AddSingleton<ICoordinatorRunnerPublisher, CoordinatorRunnerPublisher>();
builder.Services.AddSingleton<CoordinatorRunnerConnectionService>();
builder.Services.AddSingleton<IRunnerLaunchedApplicationService, RunnerLaunchedApplicationService>();
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
builder.Services.AddHostedService(sp => (RunnerLaunchedApplicationService)sp.GetRequiredService<IRunnerLaunchedApplicationService>());

var app = builder.Build();

app.MapRunnerEndpoints();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("AgentDeck Runner started — listening on port {Port}", runnerOptions.Port);
    logger.LogInformation("Workspace root: {Root}",
        app.Services.GetRequiredService<IWorkspaceService>().GetWorkspaceRoot());
});

app.Run();
