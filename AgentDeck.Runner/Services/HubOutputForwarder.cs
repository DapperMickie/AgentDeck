using AgentDeck.Runner.Hubs;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Hubs;
using AgentDeck.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace AgentDeck.Runner.Services;

/// <summary>Wires PTY process events (output, exit) to the SignalR hub.</summary>
public sealed class HubOutputForwarder : IHostedService
{
    private const int OutputTailLimit = 4096;
    private readonly IPtyProcessManager _ptyManager;
    private readonly IAgentSessionStore _sessionStore;
    private readonly IHubContext<AgentHub, IAgentHubClient> _hubContext;
    private readonly CoordinatorRunnerConnectionService _coordinatorConnection;
    private readonly ILogger<HubOutputForwarder> _logger;
    private readonly ConcurrentDictionary<string, string> _recentOutputBySession = new();

    public HubOutputForwarder(
        IPtyProcessManager ptyManager,
        IAgentSessionStore sessionStore,
        IHubContext<AgentHub, IAgentHubClient> hubContext,
        CoordinatorRunnerConnectionService coordinatorConnection,
        ILogger<HubOutputForwarder> logger)
    {
        _ptyManager = ptyManager;
        _sessionStore = sessionStore;
        _hubContext = hubContext;
        _coordinatorConnection = coordinatorConnection;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ptyManager.OutputReceived += OnOutputReceived;
        _ptyManager.ProcessExited += OnProcessExited;
        _logger.LogInformation("HubOutputForwarder started — wiring PTY events to SignalR");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ptyManager.OutputReceived -= OnOutputReceived;
        _ptyManager.ProcessExited -= OnProcessExited;
        return Task.CompletedTask;
    }

    private void OnOutputReceived(object? sender, (string SessionId, string Data) e)
    {
        _recentOutputBySession.AddOrUpdate(
            e.SessionId,
            _ => TruncateTail(e.Data),
            (_, existing) => TruncateTail(existing + e.Data));

        var output = new TerminalOutput { SessionId = e.SessionId, Data = e.Data };
        _ = _hubContext.Clients.Group(e.SessionId).ReceiveOutputAsync(output);
        _ = _coordinatorConnection.PublishTerminalOutputAsync(output);
    }

    private void OnProcessExited(object? sender, (string SessionId, int ExitCode) e)
    {
        var session = _sessionStore.Get(e.SessionId);
        var recentOutput = _recentOutputBySession.TryRemove(e.SessionId, out var outputTail)
            ? outputTail
            : string.Empty;

        if (e.ExitCode == 0)
        {
            _logger.LogInformation(
                "Session {SessionId} process exited cleanly (code {ExitCode}): name={Name}, command={Command}, arguments={Arguments}, workingDirectory={WorkingDirectory}",
                e.SessionId,
                e.ExitCode,
                session?.Name ?? "<unknown>",
                session?.Command ?? "<unknown>",
                FormatArguments(session?.Arguments),
                session?.WorkingDirectory ?? "<unknown>");
        }
        else
        {
            _logger.LogWarning(
                "Session {SessionId} process exited with code {ExitCode}: name={Name}, command={Command}, arguments={Arguments}, workingDirectory={WorkingDirectory}, recentOutput={RecentOutput}",
                e.SessionId,
                e.ExitCode,
                session?.Name ?? "<unknown>",
                session?.Command ?? "<unknown>",
                FormatArguments(session?.Arguments),
                session?.WorkingDirectory ?? "<unknown>",
                string.IsNullOrWhiteSpace(recentOutput) ? "<none>" : recentOutput);
        }

        if (session is not null)
        {
            session.Status = e.ExitCode == 0 ? TerminalStatus.Stopped : TerminalStatus.Error;
            session.ExitCode = e.ExitCode;
            _sessionStore.Update(session);
            _ = _hubContext.Clients.All.SessionUpdatedAsync(session);
            _ = _coordinatorConnection.PublishTerminalSessionUpdatedAsync(session);
        }
    }

    private static string FormatArguments(IReadOnlyList<string>? arguments) =>
        arguments is null || arguments.Count == 0 ? "<none>" : string.Join(" ", arguments);

    private static string TruncateTail(string value) =>
        value.Length <= OutputTailLimit ? value : value[^OutputTailLimit..];
}
