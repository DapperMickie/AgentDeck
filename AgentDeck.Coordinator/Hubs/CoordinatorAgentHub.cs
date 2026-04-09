using AgentDeck.Coordinator.Services;
using AgentDeck.Shared;
using AgentDeck.Shared.Hubs;
using AgentDeck.Shared.Models;
using Microsoft.AspNetCore.SignalR;

namespace AgentDeck.Coordinator.Hubs;

public sealed class CoordinatorAgentHub : Hub<IAgentHubClient>, ICoordinatorAgentHub
{
    private readonly ICompanionRegistryService _companions;
    private readonly IRunnerBrokerService _runners;
    private readonly ILogger<CoordinatorAgentHub> _logger;

    public CoordinatorAgentHub(
        ICompanionRegistryService companions,
        IRunnerBrokerService runners,
        ILogger<CoordinatorAgentHub> logger)
    {
        _companions = companions;
        _runners = runners;
        _logger = logger;
    }

    public override Task OnConnectedAsync()
    {
        var companionId = GetRequestedCompanionId();
        if (string.IsNullOrWhiteSpace(companionId))
        {
            throw new HubException("Coordinator companion identity is required before connecting to the hub.");
        }

        var companion = _companions.AttachConnection(companionId, Context.ConnectionId);
        _logger.LogInformation(
            "Companion {CompanionId} ({DisplayName}) connected to coordinator hub on connection {ConnectionId}",
            companion.CompanionId,
            companion.DisplayName,
            Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var companionId = _companions.GetCompanionIdByConnection(Context.ConnectionId);
        _companions.DisconnectConnection(Context.ConnectionId);
        if (exception is null)
        {
            _logger.LogInformation("Companion {CompanionId} disconnected from coordinator hub connection {ConnectionId}", companionId ?? "<unknown>", Context.ConnectionId);
        }
        else
        {
            _logger.LogWarning(exception, "Companion {CompanionId} disconnected from coordinator hub connection {ConnectionId} with error", companionId ?? "<unknown>", Context.ConnectionId);
        }

        return base.OnDisconnectedAsync(exception);
    }

    public Task AttachMachineAsync(string machineId)
    {
        var companionId = RequireCompanionId();
        _companions.AttachMachine(companionId, machineId);
        _logger.LogInformation("Companion {CompanionId} attached to machine {MachineId}", companionId, machineId);
        return Task.CompletedTask;
    }

    public Task DetachMachineAsync(string machineId)
    {
        var companionId = RequireCompanionId();
        _companions.DetachMachine(companionId, machineId);
        _logger.LogInformation("Companion {CompanionId} detached from machine {MachineId}", companionId, machineId);
        return Task.CompletedTask;
    }

    public async Task JoinSessionAsync(string sessionId)
    {
        var companionId = RequireCompanionId();
        _logger.LogInformation("Companion {CompanionId} requesting join for session {SessionId}", companionId, sessionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
        _companions.AttachSession(companionId, sessionId);
        await _runners.JoinSessionAsync(sessionId, Context.ConnectionAborted);
    }

    public async Task LeaveSessionAsync(string sessionId)
    {
        var companionId = RequireCompanionId();
        _logger.LogInformation("Companion {CompanionId} requesting leave for session {SessionId}", companionId, sessionId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId);
        _companions.DetachSession(companionId, sessionId);
        await _runners.LeaveSessionAsync(sessionId, Context.ConnectionAborted);
    }

    public Task SendInputAsync(string sessionId, string data)
    {
        var companionId = RequireCompanionId();
        _logger.LogDebug("Companion {CompanionId} sending input to session {SessionId} ({CharacterCount} chars)", companionId, sessionId, data.Length);
        return _runners.SendInputAsync(sessionId, data, Context.ConnectionAborted);
    }

    public Task ResizeTerminalAsync(string sessionId, int cols, int rows)
    {
        var companionId = RequireCompanionId();
        _logger.LogDebug("Companion {CompanionId} resizing session {SessionId} to {Cols}x{Rows}", companionId, sessionId, cols, rows);
        return _runners.ResizeTerminalAsync(sessionId, cols, rows, Context.ConnectionAborted);
    }

    public async Task<TerminalSession> CreateSessionAsync(string machineId, CreateTerminalRequest request)
    {
        var companionId = RequireCompanionId();
        _logger.LogInformation(
            "Companion {CompanionId} creating session '{SessionName}' on machine {MachineId} in {WorkingDirectory}",
            companionId,
            request.Name,
            machineId,
            request.WorkingDirectory);
        var session = await _runners.CreateSessionAsync(machineId, request, Context.ConnectionAborted);
        _companions.AttachSession(companionId, session.Id);
        return session;
    }

    public Task CloseSessionAsync(string sessionId)
    {
        var companionId = RequireCompanionId();
        _companions.DetachSession(companionId, sessionId);
        _logger.LogInformation("Companion {CompanionId} closing session {SessionId}", companionId, sessionId);
        return _runners.CloseSessionAsync(sessionId, Context.ConnectionAborted);
    }

    public Task<IReadOnlyList<TerminalSession>> GetSessionsAsync(string machineId)
    {
        var companionId = RequireCompanionId();
        _companions.AttachMachine(companionId, machineId);
        _logger.LogDebug("Companion {CompanionId} requested sessions for machine {MachineId}", companionId, machineId);
        return _runners.GetSessionsAsync(machineId, Context.ConnectionAborted);
    }

    private string RequireCompanionId() =>
        _companions.GetCompanionIdByConnection(Context.ConnectionId)
        ?? throw new HubException($"Coordinator does not recognize companion connection '{Context.ConnectionId}'.");

    private string? GetRequestedCompanionId()
    {
        var httpContext = Context.GetHttpContext();
        if (httpContext is null)
        {
            return null;
        }

        return httpContext.Request.Query[AgentDeckQueryNames.Companion].FirstOrDefault()
            ?? httpContext.Request.Headers[AgentDeckHeaderNames.Companion].FirstOrDefault();
    }
}
