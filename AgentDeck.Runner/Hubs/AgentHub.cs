using AgentDeck.Runner.Configuration;
using AgentDeck.Runner.Services;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Hubs;
using AgentDeck.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace AgentDeck.Runner.Hubs;

/// <summary>SignalR hub that brokers terminal I/O between the runner and connected companion apps.</summary>
public sealed class AgentHub : Hub<IAgentHubClient>, IAgentHub
{
    private readonly IPtyProcessManager _ptyManager;
    private readonly IAgentSessionStore _sessionStore;
    private readonly IWorkspaceService _workspaceService;
    private readonly RunnerOptions _options;
    private readonly ILogger<AgentHub> _logger;

    public AgentHub(
        IPtyProcessManager ptyManager,
        IAgentSessionStore sessionStore,
        IWorkspaceService workspaceService,
        IOptions<RunnerOptions> options,
        ILogger<AgentHub> logger)
    {
        _ptyManager = ptyManager;
        _sessionStore = sessionStore;
        _workspaceService = workspaceService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task JoinSessionAsync(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
        _logger.LogDebug("Client {ConnectionId} joined session {SessionId}", Context.ConnectionId, sessionId);
    }

    public async Task LeaveSessionAsync(string sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId);
        _logger.LogDebug("Client {ConnectionId} left session {SessionId}", Context.ConnectionId, sessionId);
    }

    public async Task SendInputAsync(string sessionId, string data)
    {
        await _ptyManager.WriteAsync(sessionId, data);
    }

    public async Task ResizeTerminalAsync(string sessionId, int cols, int rows)
    {
        await _ptyManager.ResizeAsync(sessionId, cols, rows);
    }

    public async Task<TerminalSession> CreateSessionAsync(CreateTerminalRequest request)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var workingDir = ResolveWorkingDirectory(request.WorkingDirectory);
        var command = ResolveCommand(request.Command);
        var arguments = request.Arguments;
        var commandWasSpecified = !string.IsNullOrWhiteSpace(request.Command);
        var (launchCommand, launchArguments) = ResolveLaunch(command, arguments, workingDir, commandWasSpecified);

        var session = new TerminalSession
        {
            Id = sessionId,
            Name = request.Name,
            WorkingDirectory = workingDir,
            Command = command,
            Arguments = arguments,
            Status = TerminalStatus.Running
        };

        _sessionStore.Add(session);

        try
        {
            _logger.LogInformation(
                "Creating session {SessionId} ({Name}): requestedCommand={RequestedCommand}, resolvedCommand={ResolvedCommand}, arguments={Arguments}, workingDirectory={WorkingDirectory}, launchCommand={LaunchCommand}, launchArguments={LaunchArguments}, commandWasSpecified={CommandWasSpecified}",
                sessionId,
                request.Name,
                request.Command ?? "<default>",
                command,
                FormatArguments(arguments),
                workingDir,
                launchCommand,
                FormatArguments(launchArguments),
                commandWasSpecified);
            await _ptyManager.StartAsync(sessionId, launchCommand, launchArguments, workingDir, request.Cols, request.Rows);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to start PTY for session {SessionId} ({Name}): requestedCommand={RequestedCommand}, resolvedCommand={ResolvedCommand}, arguments={Arguments}, workingDirectory={WorkingDirectory}, launchCommand={LaunchCommand}, launchArguments={LaunchArguments}",
                sessionId,
                request.Name,
                request.Command ?? "<default>",
                command,
                FormatArguments(arguments),
                workingDir,
                launchCommand,
                FormatArguments(launchArguments));
            session.Status = TerminalStatus.Error;
            _sessionStore.Update(session);
            await Clients.All.SessionCreatedAsync(session);
            throw new HubException($"Failed to start terminal process: {ex.Message}");
        }

        _logger.LogInformation("Created session {SessionId} ({Name})", sessionId, request.Name);
        await Clients.All.SessionCreatedAsync(session);
        return session;
    }

    public async Task CloseSessionAsync(string sessionId)
    {
        var session = _sessionStore.Get(sessionId);
        if (session is null) return;

        await _ptyManager.KillAsync(sessionId);
        _sessionStore.Remove(sessionId);

        session.Status = TerminalStatus.Stopped;
        _logger.LogInformation("Closed session {SessionId}", sessionId);
        await Clients.All.SessionClosedAsync(sessionId);
    }

    public Task<IReadOnlyList<TerminalSession>> GetSessionsAsync()
    {
        return Task.FromResult(_sessionStore.GetAll());
    }

    private string ResolveWorkingDirectory(string workingDirectory)
    {
        return _workspaceService.ResolvePath(workingDirectory);
    }

    private string ResolveCommand(string? command)
    {
        return !string.IsNullOrWhiteSpace(command)
            ? command
            : ShellLaunchBuilder.ResolveDefaultShell(_options.DefaultShell);
    }

    private static (string Command, IReadOnlyList<string> Arguments) ResolveLaunch(
        string command,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        bool commandWasSpecified)
    {
        return ShellLaunchBuilder.BuildInteractiveLaunch(command, arguments, workingDirectory, commandWasSpecified);
    }

    private static string FormatArguments(IReadOnlyList<string> arguments) =>
        arguments.Count == 0 ? "<none>" : string.Join(" ", arguments);
}
