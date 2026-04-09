using AgentDeck.Coordinator.Services;
using AgentDeck.Shared.Hubs;
using AgentDeck.Shared.Models;
using Microsoft.AspNetCore.SignalR;

namespace AgentDeck.Coordinator.Hubs;

public sealed class CoordinatorAgentHub : Hub<IAgentHubClient>, ICoordinatorAgentHub
{
    private readonly IRunnerBrokerService _runners;
    private readonly ILogger<CoordinatorAgentHub> _logger;

    public CoordinatorAgentHub(
        IRunnerBrokerService runners,
        ILogger<CoordinatorAgentHub> logger)
    {
        _runners = runners;
        _logger = logger;
    }

    public override Task OnConnectedAsync()
    {
        _logger.LogInformation("Companion connection {ConnectionId} connected to coordinator hub", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is null)
        {
            _logger.LogInformation("Companion connection {ConnectionId} disconnected from coordinator hub", Context.ConnectionId);
        }
        else
        {
            _logger.LogWarning(exception, "Companion connection {ConnectionId} disconnected from coordinator hub with error", Context.ConnectionId);
        }

        return base.OnDisconnectedAsync(exception);
    }

    public async Task JoinSessionAsync(string sessionId)
    {
        _logger.LogInformation("Companion connection {ConnectionId} requesting join for session {SessionId}", Context.ConnectionId, sessionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
        await _runners.JoinSessionAsync(sessionId, Context.ConnectionAborted);
    }

    public async Task LeaveSessionAsync(string sessionId)
    {
        _logger.LogInformation("Companion connection {ConnectionId} requesting leave for session {SessionId}", Context.ConnectionId, sessionId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId);
        await _runners.LeaveSessionAsync(sessionId, Context.ConnectionAborted);
    }

    public Task SendInputAsync(string sessionId, string data)
    {
        _logger.LogDebug("Companion connection {ConnectionId} sending input to session {SessionId} ({CharacterCount} chars)", Context.ConnectionId, sessionId, data.Length);
        return _runners.SendInputAsync(sessionId, data, Context.ConnectionAborted);
    }

    public Task ResizeTerminalAsync(string sessionId, int cols, int rows)
    {
        _logger.LogDebug("Companion connection {ConnectionId} resizing session {SessionId} to {Cols}x{Rows}", Context.ConnectionId, sessionId, cols, rows);
        return _runners.ResizeTerminalAsync(sessionId, cols, rows, Context.ConnectionAborted);
    }

    public Task<TerminalSession> CreateSessionAsync(string machineId, CreateTerminalRequest request)
    {
        _logger.LogInformation(
            "Companion connection {ConnectionId} creating session '{SessionName}' on machine {MachineId} in {WorkingDirectory}",
            Context.ConnectionId,
            request.Name,
            machineId,
            request.WorkingDirectory);
        return _runners.CreateSessionAsync(machineId, request, Context.ConnectionAborted);
    }

    public Task CloseSessionAsync(string sessionId)
    {
        _logger.LogInformation("Companion connection {ConnectionId} closing session {SessionId}", Context.ConnectionId, sessionId);
        return _runners.CloseSessionAsync(sessionId, Context.ConnectionAborted);
    }

    public Task<IReadOnlyList<TerminalSession>> GetSessionsAsync(string machineId)
    {
        _logger.LogDebug("Companion connection {ConnectionId} requested sessions for machine {MachineId}", Context.ConnectionId, machineId);
        return _runners.GetSessionsAsync(machineId, Context.ConnectionAborted);
    }
}
