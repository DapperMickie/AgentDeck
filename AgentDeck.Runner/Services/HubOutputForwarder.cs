using AgentDeck.Runner.Hubs;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Hubs;
using AgentDeck.Shared.Models;
using Microsoft.AspNetCore.SignalR;

namespace AgentDeck.Runner.Services;

/// <summary>Wires PTY process events (output, exit) to the SignalR hub.</summary>
public sealed class HubOutputForwarder : IHostedService
{
    private readonly IPtyProcessManager _ptyManager;
    private readonly IAgentSessionStore _sessionStore;
    private readonly IHubContext<AgentHub, IAgentHubClient> _hubContext;
    private readonly ILogger<HubOutputForwarder> _logger;

    public HubOutputForwarder(
        IPtyProcessManager ptyManager,
        IAgentSessionStore sessionStore,
        IHubContext<AgentHub, IAgentHubClient> hubContext,
        ILogger<HubOutputForwarder> logger)
    {
        _ptyManager = ptyManager;
        _sessionStore = sessionStore;
        _hubContext = hubContext;
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
        var output = new TerminalOutput { SessionId = e.SessionId, Data = e.Data };
        _ = _hubContext.Clients.Group(e.SessionId).ReceiveOutputAsync(output);
    }

    private void OnProcessExited(object? sender, (string SessionId, int ExitCode) e)
    {
        _logger.LogInformation("Session {SessionId} process exited (code {ExitCode})", e.SessionId, e.ExitCode);

        var session = _sessionStore.Get(e.SessionId);
        if (session is not null)
        {
            session.Status = e.ExitCode == 0 ? TerminalStatus.Stopped : TerminalStatus.Error;
            session.ExitCode = e.ExitCode;
            _sessionStore.Update(session);
            _ = _hubContext.Clients.All.SessionUpdatedAsync(session);
        }
    }
}
