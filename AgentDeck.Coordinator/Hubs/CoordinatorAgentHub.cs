using AgentDeck.Coordinator.Services;
using AgentDeck.Shared.Hubs;
using AgentDeck.Shared.Models;
using Microsoft.AspNetCore.SignalR;

namespace AgentDeck.Coordinator.Hubs;

public sealed class CoordinatorAgentHub : Hub<IAgentHubClient>, ICoordinatorAgentHub
{
    private readonly IRunnerBrokerService _runners;

    public CoordinatorAgentHub(IRunnerBrokerService runners)
    {
        _runners = runners;
    }

    public async Task JoinSessionAsync(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
        await _runners.JoinSessionAsync(sessionId, Context.ConnectionAborted);
    }

    public async Task LeaveSessionAsync(string sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId);
        await _runners.LeaveSessionAsync(sessionId, Context.ConnectionAborted);
    }

    public Task SendInputAsync(string sessionId, string data) =>
        _runners.SendInputAsync(sessionId, data, Context.ConnectionAborted);

    public Task ResizeTerminalAsync(string sessionId, int cols, int rows) =>
        _runners.ResizeTerminalAsync(sessionId, cols, rows, Context.ConnectionAborted);

    public Task<TerminalSession> CreateSessionAsync(string machineId, CreateTerminalRequest request) =>
        _runners.CreateSessionAsync(machineId, request, Context.ConnectionAborted);

    public Task CloseSessionAsync(string sessionId) =>
        _runners.CloseSessionAsync(sessionId, Context.ConnectionAborted);

    public Task<IReadOnlyList<TerminalSession>> GetSessionsAsync(string machineId) =>
        _runners.GetSessionsAsync(machineId, Context.ConnectionAborted);
}
