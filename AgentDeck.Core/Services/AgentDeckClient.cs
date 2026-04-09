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
    private string? _coordinatorUrl;

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

    public async Task ConnectAsync(string coordinatorUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coordinatorUrl);
        var normalizedCoordinatorUrl = coordinatorUrl.Trim().TrimEnd('/');

        if (_connection is not null &&
            string.Equals(_coordinatorUrl, normalizedCoordinatorUrl, StringComparison.OrdinalIgnoreCase) &&
            ConnectionState is HubConnectionState.Connected or HubConnectionState.Connecting or HubConnectionState.Reconnecting)
        {
            return;
        }

        if (_connection is not null)
        {
            await DisconnectAsync();
        }

        var hubUrl = $"{normalizedCoordinatorUrl}/hubs/agent";
        _logger.LogInformation("Connecting to coordinator hub at {Url}", hubUrl);
        SetState(HubConnectionState.Connecting);

        _http.Dispose();
        _http = new HttpClient { BaseAddress = new Uri($"{normalizedCoordinatorUrl}/", UriKind.Absolute) };
        _coordinatorUrl = normalizedCoordinatorUrl;

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
            _logger.LogInformation("Connected to coordinator hub");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to coordinator hub at {Url}", hubUrl);
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
        _coordinatorUrl = null;
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

    public async Task<TerminalSession?> CreateSessionAsync(string machineId, CreateTerminalRequest request)
    {
        if (_connection is null || _connection.State != Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected)
            return null;

        try
        {
            return await _connection.InvokeAsync<TerminalSession>(nameof(ICoordinatorAgentHub.CreateSessionAsync), machineId, request);
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

    public async Task<IReadOnlyList<TerminalSession>> GetSessionsAsync(string machineId)
    {
        if (_connection is null || _connection.State != Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected)
            return [];

        try
        {
            return await _connection.InvokeAsync<IReadOnlyList<TerminalSession>>(nameof(ICoordinatorAgentHub.GetSessionsAsync), machineId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetSessionsAsync failed");
            return [];
        }
    }

    public async Task<WorkspaceInfo?> GetWorkspaceAsync(string machineId, CancellationToken ct = default)
    {
        if (_http.BaseAddress is null) return null;
        try
        {
            return await _http.GetFromJsonAsync<WorkspaceInfo>($"/api/machines/{Uri.EscapeDataString(machineId)}/workspace", ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetWorkspaceAsync failed");
            return null;
        }
    }

    public async Task<MachineCapabilitiesSnapshot?> GetMachineCapabilitiesAsync(string machineId, CancellationToken ct = default)
    {
        if (_http.BaseAddress is null) return null;
        try
        {
            return await _http.GetFromJsonAsync<MachineCapabilitiesSnapshot>($"/api/machines/{Uri.EscapeDataString(machineId)}/capabilities", ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetMachineCapabilitiesAsync failed");
            return null;
        }
    }

    public async Task<MachineCapabilityInstallResult?> InstallMachineCapabilityAsync(string machineId, string capabilityId, string? version = null, CancellationToken ct = default)
    {
        if (_http.BaseAddress is null) return null;
        try
        {
            var request = new MachineCapabilityInstallRequest
            {
                Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim()
            };
            using var response = await _http.PostAsJsonAsync($"/api/machines/{Uri.EscapeDataString(machineId)}/capabilities/{Uri.EscapeDataString(capabilityId)}/install", request, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<MachineCapabilityInstallResult>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InstallMachineCapabilityAsync failed for {CapabilityId}", capabilityId);
            return null;
        }
    }

    public async Task<MachineCapabilityInstallResult?> UpdateMachineCapabilityAsync(string machineId, string capabilityId, CancellationToken ct = default)
    {
        if (_http.BaseAddress is null) return null;
        try
        {
            using var response = await _http.PostAsync($"/api/machines/{Uri.EscapeDataString(machineId)}/capabilities/{Uri.EscapeDataString(capabilityId)}/update", null, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<MachineCapabilityInstallResult>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateMachineCapabilityAsync failed for {CapabilityId}", capabilityId);
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
