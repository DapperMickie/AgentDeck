using System.Net.Http.Json;
using AgentDeck.Shared.Hubs;
using AgentDeck.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace AgentDeck.Core.Services;

/// <inheritdoc />
public sealed class AgentDeckClient : IAgentDeckClient, IAsyncDisposable
{
    private readonly ILogger<AgentDeckClient> _logger;
    private HubConnection? _connection;
    private HttpClient _http = new();

    public HubConnectionState ConnectionState { get; private set; } = HubConnectionState.Disconnected;

    public event EventHandler<HubConnectionState>? ConnectionStateChanged;
    public event EventHandler<TerminalOutput>? OutputReceived;
    public event EventHandler<TerminalSession>? SessionCreated;
    public event EventHandler<TerminalSession>? SessionUpdated;
    public event EventHandler<string>? SessionClosed;

    public AgentDeckClient(ILogger<AgentDeckClient> logger)
    {
        _logger = logger;
    }

    public async Task ConnectAsync(string hubUrl, CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
            await DisconnectAsync();

        _logger.LogInformation("Connecting to runner hub at {Url}", hubUrl);
        SetState(HubConnectionState.Connecting);

        // Derive the REST base URL from the hub URL and refresh the HttpClient
        var hubUri = new Uri(hubUrl);
        var baseUri = new Uri($"{hubUri.Scheme}://{hubUri.Host}:{hubUri.Port}/");
        _http.Dispose();
        _http = new HttpClient { BaseAddress = baseUri };

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        // Wire client callbacks
        _connection.On<TerminalOutput>(nameof(IAgentHubClient.ReceiveOutputAsync),
            output => OutputReceived?.Invoke(this, output));

        _connection.On<TerminalSession>(nameof(IAgentHubClient.SessionCreatedAsync),
            session => SessionCreated?.Invoke(this, session));

        _connection.On<TerminalSession>(nameof(IAgentHubClient.SessionUpdatedAsync),
            session => SessionUpdated?.Invoke(this, session));

        _connection.On<string>(nameof(IAgentHubClient.SessionClosedAsync),
            sessionId => SessionClosed?.Invoke(this, sessionId));

        _connection.Reconnecting += _ => { SetState(HubConnectionState.Reconnecting); return Task.CompletedTask; };
        _connection.Reconnected += _ => { SetState(HubConnectionState.Connected); return Task.CompletedTask; };
        _connection.Closed += _ => { SetState(HubConnectionState.Disconnected); return Task.CompletedTask; };

        try
        {
            await _connection.StartAsync(cancellationToken);
            SetState(HubConnectionState.Connected);
            _logger.LogInformation("Connected to runner hub");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to runner hub at {Url}", hubUrl);
            SetState(HubConnectionState.Disconnected);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_connection is null) return;
        await _connection.StopAsync();
        await _connection.DisposeAsync();
        _connection = null;
        SetState(HubConnectionState.Disconnected);
    }

    public Task JoinSessionAsync(string sessionId) =>
        InvokeAsync(nameof(IAgentHub.JoinSessionAsync), sessionId);

    public Task LeaveSessionAsync(string sessionId) =>
        InvokeAsync(nameof(IAgentHub.LeaveSessionAsync), sessionId);

    public Task SendInputAsync(string sessionId, string data) =>
        InvokeAsync(nameof(IAgentHub.SendInputAsync), sessionId, data);

    public Task ResizeTerminalAsync(string sessionId, int cols, int rows) =>
        InvokeAsync(nameof(IAgentHub.ResizeTerminalAsync), sessionId, cols, rows);

    public async Task<TerminalSession?> CreateSessionAsync(CreateTerminalRequest request)
    {
        if (_connection is null || _connection.State != Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected)
            return null;

        try
        {
            return await _connection.InvokeAsync<TerminalSession>(nameof(IAgentHub.CreateSessionAsync), request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateSessionAsync failed");
            return null;
        }
    }

    public async Task CloseSessionAsync(string sessionId)
    {
        if (_connection is null) return;
        try { await _connection.InvokeAsync(nameof(IAgentHub.CloseSessionAsync), sessionId); }
        catch (Exception ex) { _logger.LogError(ex, "CloseSessionAsync failed for {SessionId}", sessionId); }
    }

    public async Task<IReadOnlyList<TerminalSession>> GetSessionsAsync()
    {
        if (_connection is null || _connection.State != Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected)
            return [];

        try
        {
            return await _connection.InvokeAsync<IReadOnlyList<TerminalSession>>(nameof(IAgentHub.GetSessionsAsync));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetSessionsAsync failed");
            return [];
        }
    }

    public async Task<WorkspaceInfo?> GetWorkspaceAsync(CancellationToken ct = default)
    {
        if (_http.BaseAddress is null) return null;
        try
        {
            return await _http.GetFromJsonAsync<WorkspaceInfo>("/api/workspace", ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetWorkspaceAsync failed");
            return null;
        }
    }

    public async Task<MachineCapabilitiesSnapshot?> GetMachineCapabilitiesAsync(CancellationToken ct = default)
    {
        if (_http.BaseAddress is null) return null;
        try
        {
            return await _http.GetFromJsonAsync<MachineCapabilitiesSnapshot>("/api/capabilities", ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetMachineCapabilitiesAsync failed");
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
        _http.Dispose();
    }

    private void SetState(HubConnectionState state)
    {
        if (ConnectionState == state) return;
        ConnectionState = state;
        ConnectionStateChanged?.Invoke(this, state);
    }

    private async Task InvokeAsync(string method, params object?[] args)
    {
        if (_connection is null || _connection.State != Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected)
            return;
        try { await _connection.SendCoreAsync(method, args); }
        catch (Exception ex) { _logger.LogError(ex, "Hub invocation failed: {Method}", method); }
    }
}
