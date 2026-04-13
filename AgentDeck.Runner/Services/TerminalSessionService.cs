using System.Collections.Concurrent;
using AgentDeck.Runner.Configuration;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;
using Microsoft.Extensions.Options;

namespace AgentDeck.Runner.Services;

public sealed class TerminalSessionService : ITerminalSessionService
{
    private readonly IPtyProcessManager _ptyManager;
    private readonly IAgentSessionStore _sessionStore;
    private readonly IWorkspaceService _workspaceService;
    private readonly RunnerOptions _options;
    private readonly ILogger<TerminalSessionService> _logger;
    private readonly ConcurrentDictionary<string, Task<TerminalSession>> _pendingCreates = new(StringComparer.OrdinalIgnoreCase);

    public TerminalSessionService(
        IPtyProcessManager ptyManager,
        IAgentSessionStore sessionStore,
        IWorkspaceService workspaceService,
        IOptions<RunnerOptions> options,
        ILogger<TerminalSessionService> logger)
    {
        _ptyManager = ptyManager;
        _sessionStore = sessionStore;
        _workspaceService = workspaceService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<TerminalSession> CreateSessionAsync(CreateTerminalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrWhiteSpace(request.RequestedSessionId))
        {
            var requestedSessionId = request.RequestedSessionId.Trim();
            if (_sessionStore.Get(requestedSessionId) is { } existingSession &&
                _ptyManager.IsActive(requestedSessionId))
            {
                _logger.LogInformation(
                    "Reusing existing terminal session {SessionId} ({Name}) for idempotent create request.",
                    existingSession.Id,
                    existingSession.Name);
                return existingSession;
            }

            var createTask = _pendingCreates.GetOrAdd(
                requestedSessionId,
                _ => CreateSessionCoreAsync(request, requestedSessionId, cancellationToken));
            try
            {
                return await createTask;
            }
            finally
            {
                _pendingCreates.TryRemove(requestedSessionId, out _);
            }
        }

        return await CreateSessionCoreAsync(request, Guid.NewGuid().ToString("N"), cancellationToken);
    }

    private async Task<TerminalSession> CreateSessionCoreAsync(CreateTerminalRequest request, string sessionId, CancellationToken cancellationToken)
    {
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
            await _ptyManager.StartAsync(sessionId, launchCommand, launchArguments, workingDir, request.Cols, request.Rows, cancellationToken);
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
            _sessionStore.Remove(sessionId);
            throw new TerminalSessionStartException(session, ex);
        }

        _logger.LogInformation("Created session {SessionId} ({Name})", sessionId, request.Name);
        return session;
    }

    private string ResolveWorkingDirectory(string workingDirectory) => _workspaceService.ResolvePath(workingDirectory);

    private string ResolveCommand(string? command) =>
        !string.IsNullOrWhiteSpace(command)
            ? command
            : ShellLaunchBuilder.ResolveDefaultShell(_options.DefaultShell);

    private static (string Command, IReadOnlyList<string> Arguments) ResolveLaunch(
        string command,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        bool commandWasSpecified) =>
        ShellLaunchBuilder.BuildInteractiveLaunch(command, arguments, workingDirectory, commandWasSpecified);

    private static string FormatArguments(IReadOnlyList<string> arguments) =>
        arguments.Count == 0 ? "<none>" : string.Join(" ", arguments);
}
