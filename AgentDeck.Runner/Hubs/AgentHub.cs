using AgentDeck.Runner.Services;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Hubs;
using AgentDeck.Shared.Models;
using Microsoft.AspNetCore.SignalR;

namespace AgentDeck.Runner.Hubs;

/// <summary>SignalR hub that brokers terminal I/O between the runner and connected companion apps.</summary>
public sealed class AgentHub : Hub<IAgentHubClient>, IAgentHub
{
    private readonly IPtyProcessManager _ptyManager;
    private readonly IAgentSessionStore _sessionStore;
    private readonly ITerminalSessionService _terminalSessions;
    private readonly ILogger<AgentHub> _logger;

    public AgentHub(
        IPtyProcessManager ptyManager,
        IAgentSessionStore sessionStore,
        ITerminalSessionService terminalSessions,
        ILogger<AgentHub> logger)
    {
        _ptyManager = ptyManager;
        _sessionStore = sessionStore;
        _terminalSessions = terminalSessions;
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
        try
        {
            var session = await _terminalSessions.CreateSessionAsync(request, Context.ConnectionAborted);
            await Clients.All.SessionCreatedAsync(session);
            return session;
        }
        catch (TerminalSessionStartException ex)
        {
            _logger.LogError(
                ex.InnerException ?? ex,
                "Failed to create terminal session ({Name}) via hub: requestedCommand={RequestedCommand}, workingDirectory={WorkingDirectory}",
                request.Name,
                request.Command ?? "<default>",
                request.WorkingDirectory);
            await Clients.All.SessionCreatedAsync(ex.Session);
            throw new HubException($"Failed to start terminal process: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to create terminal session ({Name}) via hub: requestedCommand={RequestedCommand}, workingDirectory={WorkingDirectory}",
                request.Name,
                request.Command ?? "<default>",
                request.WorkingDirectory);
            throw new HubException(ex.Message);
        }
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

}
