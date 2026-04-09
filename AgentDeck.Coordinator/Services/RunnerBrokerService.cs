using System.Collections.Concurrent;
using System.Net.Http.Json;
using AgentDeck.Coordinator.Hubs;
using AgentDeck.Shared;
using AgentDeck.Shared.Hubs;
using AgentDeck.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace AgentDeck.Coordinator.Services;

public sealed class RunnerBrokerService : IRunnerBrokerService, IAsyncDisposable
{
    private sealed class RunnerEntry
    {
        public required string MachineId { get; init; }
        public required SemaphoreSlim Gate { get; init; }
        public RegisteredRunnerMachine? Machine { get; set; }
        public string RunnerUrl { get; set; } = string.Empty;
        public HttpClient? HttpClient { get; set; }
        public HubConnection? HubConnection { get; set; }
    }

    private readonly ConcurrentDictionary<string, RunnerEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _sessionMachineMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly ICompanionRegistryService _companions;
    private readonly IWorkerRegistryService _registry;
    private readonly IHubContext<CoordinatorAgentHub, IAgentHubClient> _hubContext;
    private readonly ILogger<RunnerBrokerService> _logger;

    public RunnerBrokerService(
        ICompanionRegistryService companions,
        IWorkerRegistryService registry,
        IHubContext<CoordinatorAgentHub, IAgentHubClient> hubContext,
        ILogger<RunnerBrokerService> logger)
    {
        _companions = companions;
        _registry = registry;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task<TerminalSession> CreateSessionAsync(string machineId, CreateTerminalRequest request, CancellationToken cancellationToken = default)
    {
        var entry = await EnsureEntryAsync(machineId, cancellationToken);
        _logger.LogInformation(
            "Brokering session creation '{SessionName}' for machine {MachineName} ({MachineId}) in {WorkingDirectory}",
            request.Name,
            entry.Machine?.MachineName ?? machineId,
            machineId,
            request.WorkingDirectory);
        var session = await entry.HubConnection!.InvokeAsync<TerminalSession>(
            nameof(IAgentHub.CreateSessionAsync),
            request,
            cancellationToken);

        return AnnotateSession(session, entry.Machine!);
    }

    public async Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var entry = await ResolveEntryBySessionAsync(sessionId, cancellationToken);
        _logger.LogInformation(
            "Brokering close for session {SessionId} on machine {MachineName} ({MachineId})",
            sessionId,
            entry.Machine?.MachineName ?? entry.MachineId,
            entry.MachineId);
        await entry.HubConnection!.InvokeAsync(nameof(IAgentHub.CloseSessionAsync), sessionId, cancellationToken);
    }

    public async Task<IReadOnlyList<TerminalSession>> GetSessionsAsync(string machineId, CancellationToken cancellationToken = default)
    {
        var entry = await EnsureEntryAsync(machineId, cancellationToken);
        _logger.LogDebug("Brokering session list request for machine {MachineName} ({MachineId})", entry.Machine?.MachineName ?? machineId, machineId);
        var sessions = await entry.HubConnection!.InvokeAsync<IReadOnlyList<TerminalSession>>(
            nameof(IAgentHub.GetSessionsAsync),
            cancellationToken);

        return sessions.Select(session => AnnotateSession(session, entry.Machine!)).ToArray();
    }

    public async Task JoinSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var entry = await ResolveEntryBySessionAsync(sessionId, cancellationToken);
        _logger.LogDebug("Broker joining session {SessionId} on machine {MachineName} ({MachineId})", sessionId, entry.Machine?.MachineName ?? entry.MachineId, entry.MachineId);
        await entry.HubConnection!.InvokeAsync(nameof(IAgentHub.JoinSessionAsync), sessionId, cancellationToken);
    }

    public async Task LeaveSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var entry = await TryResolveEntryBySessionAsync(sessionId, cancellationToken);
        if (entry is null)
        {
            _logger.LogDebug("Broker leave ignored for unknown session {SessionId}", sessionId);
            return;
        }

        _logger.LogDebug("Broker leaving session {SessionId} on machine {MachineName} ({MachineId})", sessionId, entry.Machine?.MachineName ?? entry.MachineId, entry.MachineId);
        await entry.HubConnection!.InvokeAsync(nameof(IAgentHub.LeaveSessionAsync), sessionId, cancellationToken);
    }

    public async Task ResizeTerminalAsync(string sessionId, int cols, int rows, CancellationToken cancellationToken = default)
    {
        var entry = await ResolveEntryBySessionAsync(sessionId, cancellationToken);
        _logger.LogDebug("Broker resizing session {SessionId} on machine {MachineName} ({MachineId}) to {Cols}x{Rows}", sessionId, entry.Machine?.MachineName ?? entry.MachineId, entry.MachineId, cols, rows);
        await entry.HubConnection!.InvokeAsync(nameof(IAgentHub.ResizeTerminalAsync), sessionId, cols, rows, cancellationToken);
    }

    public async Task SendInputAsync(string sessionId, string data, CancellationToken cancellationToken = default)
    {
        var entry = await ResolveEntryBySessionAsync(sessionId, cancellationToken);
        _logger.LogDebug("Broker sending input to session {SessionId} on machine {MachineName} ({MachineId}) ({CharacterCount} chars)", sessionId, entry.Machine?.MachineName ?? entry.MachineId, entry.MachineId, data.Length);
        await entry.HubConnection!.InvokeAsync(nameof(IAgentHub.SendInputAsync), sessionId, data, cancellationToken);
    }

    public async Task<WorkspaceInfo?> GetWorkspaceAsync(string machineId, CancellationToken cancellationToken = default)
    {
        var entry = await EnsureEntryAsync(machineId, cancellationToken);
        _logger.LogInformation("Brokering workspace request for machine {MachineName} ({MachineId})", entry.Machine?.MachineName ?? machineId, machineId);
        return await entry.HttpClient!.GetFromJsonAsync<WorkspaceInfo>("api/workspace", cancellationToken);
    }

    public async Task<OpenProjectOnRunnerResult?> OpenProjectAsync(string machineId, OpenProjectOnRunnerRequest request, string actorId, CancellationToken cancellationToken = default)
    {
        var entry = await EnsureEntryAsync(machineId, cancellationToken);
        _logger.LogInformation(
            "Brokering project open for {ProjectId} on machine {MachineName} ({MachineId}) requested by {ActorId}",
            request.ProjectId,
            entry.Machine?.MachineName ?? machineId,
            machineId,
            actorId);
        using var response = await CreateRunnerRequest(entry, HttpMethod.Post, "api/projects/open", actorId)
            .WithJsonContent(request)
            .SendAsync(entry.HttpClient!, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            var message = string.IsNullOrWhiteSpace(responseText)
                ? $"Runner open-project request failed with HTTP {(int)response.StatusCode}."
                : responseText;
            throw new InvalidOperationException(message);
        }

        return await response.Content.ReadFromJsonAsync<OpenProjectOnRunnerResult>(cancellationToken: cancellationToken);
    }

    public async Task<MachineCapabilitiesSnapshot?> GetMachineCapabilitiesAsync(string machineId, CancellationToken cancellationToken = default)
    {
        var entry = await EnsureEntryAsync(machineId, cancellationToken);
        _logger.LogInformation("Brokering capability snapshot request for machine {MachineName} ({MachineId})", entry.Machine?.MachineName ?? machineId, machineId);
        return await entry.HttpClient!.GetFromJsonAsync<MachineCapabilitiesSnapshot>("api/capabilities", cancellationToken);
    }

    public async Task<MachineCapabilityInstallResult?> InstallMachineCapabilityAsync(string machineId, string capabilityId, MachineCapabilityInstallRequest request, string actorId, CancellationToken cancellationToken = default)
    {
        var entry = await EnsureEntryAsync(machineId, cancellationToken);
        _logger.LogInformation(
            "Brokering capability install {CapabilityId} for machine {MachineName} ({MachineId}) requested by {ActorId} version {RequestedVersion}",
            capabilityId,
            entry.Machine?.MachineName ?? machineId,
            machineId,
            actorId,
            request.Version ?? "<default>");
        using var response = await CreateRunnerRequest(entry, HttpMethod.Post, $"api/capabilities/{Uri.EscapeDataString(capabilityId)}/install", actorId)
            .WithJsonContent(request)
            .SendAsync(entry.HttpClient!, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MachineCapabilityInstallResult>(cancellationToken: cancellationToken);
    }

    public async Task<MachineCapabilityInstallResult?> UpdateMachineCapabilityAsync(string machineId, string capabilityId, string actorId, CancellationToken cancellationToken = default)
    {
        var entry = await EnsureEntryAsync(machineId, cancellationToken);
        _logger.LogInformation(
            "Brokering capability update {CapabilityId} for machine {MachineName} ({MachineId}) requested by {ActorId}",
            capabilityId,
            entry.Machine?.MachineName ?? machineId,
            machineId,
            actorId);
        using var response = await CreateRunnerRequest(entry, HttpMethod.Post, $"api/capabilities/{Uri.EscapeDataString(capabilityId)}/update", actorId)
            .SendAsync(entry.HttpClient!, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MachineCapabilityInstallResult>(cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _entries.Values)
        {
            if (entry.HubConnection is not null)
            {
                await entry.HubConnection.DisposeAsync();
            }

            entry.HttpClient?.Dispose();
            entry.Gate.Dispose();
        }

        _entries.Clear();
        _sessionMachineMap.Clear();
    }

    private async Task<RunnerEntry> EnsureEntryAsync(string machineId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);

        var machine = await _registry.GetMachineAsync(machineId, cancellationToken)
            ?? throw new InvalidOperationException($"Coordinator does not know runner machine '{machineId}'.");

        if (string.IsNullOrWhiteSpace(machine.RunnerUrl))
        {
            throw new InvalidOperationException($"Runner machine '{machine.MachineName}' does not advertise a runner URL for coordinator brokering.");
        }

        var normalizedRunnerUrl = machine.RunnerUrl.Trim().TrimEnd('/');
        var entry = _entries.GetOrAdd(machine.MachineId, static id => new RunnerEntry
        {
            MachineId = id,
            Gate = new SemaphoreSlim(1, 1)
        });

        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            entry.Machine = machine;
            if (entry.HubConnection is not null &&
                string.Equals(entry.RunnerUrl, normalizedRunnerUrl, StringComparison.OrdinalIgnoreCase) &&
                entry.HubConnection.State is HubConnectionState.Connected or HubConnectionState.Connecting or HubConnectionState.Reconnecting)
            {
                return entry;
            }

            if (entry.HubConnection is not null)
            {
                await entry.HubConnection.DisposeAsync();
                entry.HubConnection = null;
            }

            entry.HttpClient?.Dispose();
            entry.HttpClient = new HttpClient
            {
                BaseAddress = new Uri($"{normalizedRunnerUrl}/", UriKind.Absolute)
            };
            entry.RunnerUrl = normalizedRunnerUrl;

            var hubConnection = new HubConnectionBuilder()
                .WithUrl($"{normalizedRunnerUrl}/hubs/agent")
                .WithAutomaticReconnect()
                .Build();

            RegisterRunnerCallbacks(hubConnection, entry);
            await hubConnection.StartAsync(cancellationToken);
            entry.HubConnection = hubConnection;
            _logger.LogInformation("Coordinator connected broker channel to runner {MachineName} at {RunnerUrl}", machine.MachineName, normalizedRunnerUrl);
            return entry;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private void RegisterRunnerCallbacks(HubConnection hubConnection, RunnerEntry entry)
    {
        hubConnection.On<TerminalOutput>(
            nameof(IAgentHubClient.ReceiveOutputAsync),
            output => _hubContext.Clients.Group(output.SessionId).ReceiveOutputAsync(output));

        hubConnection.On<TerminalSession>(
            nameof(IAgentHubClient.SessionCreatedAsync),
            session => _hubContext.Clients.All.SessionCreatedAsync(AnnotateSession(session, entry.Machine!)));

        hubConnection.On<TerminalSession>(
            nameof(IAgentHubClient.SessionUpdatedAsync),
            session => _hubContext.Clients.All.SessionUpdatedAsync(AnnotateSession(session, entry.Machine!)));

        hubConnection.On<string>(
            nameof(IAgentHubClient.SessionClosedAsync),
            sessionId =>
            {
                _sessionMachineMap.TryRemove(sessionId, out _);
                _companions.RemoveSessionFromAll(sessionId);
                _logger.LogInformation("Runner reported closed session {SessionId} on machine {MachineName} ({MachineId})", sessionId, entry.Machine?.MachineName ?? entry.MachineId, entry.MachineId);
                return _hubContext.Clients.All.SessionClosedAsync(sessionId);
            });
    }

    private TerminalSession AnnotateSession(TerminalSession session, RegisteredRunnerMachine machine)
    {
        _sessionMachineMap[session.Id] = machine.MachineId;
        return new TerminalSession
        {
            Id = session.Id,
            Name = session.Name,
            WorkingDirectory = session.WorkingDirectory,
            Command = session.Command,
            Arguments = session.Arguments,
            Status = session.Status,
            ExitCode = session.ExitCode,
            CreatedAt = session.CreatedAt,
            MachineId = machine.MachineId,
            MachineName = machine.MachineName
        };
    }

    private async Task<RunnerEntry> ResolveEntryBySessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (await TryResolveEntryBySessionAsync(sessionId, cancellationToken) is { } entry)
        {
            return entry;
        }

        _logger.LogWarning("Coordinator could not resolve runner ownership for session {SessionId}", sessionId);
        throw new InvalidOperationException($"Coordinator could not resolve runner ownership for session '{sessionId}'.");
    }

    private async Task<RunnerEntry?> TryResolveEntryBySessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_sessionMachineMap.TryGetValue(sessionId, out var machineId))
        {
            return await EnsureEntryAsync(machineId, cancellationToken);
        }

        var machines = await _registry.GetMachinesAsync(cancellationToken);
        foreach (var machine in machines.Where(machine => machine.IsOnline))
        {
            var entry = await EnsureEntryAsync(machine.MachineId, cancellationToken);
            var sessions = await entry.HubConnection!.InvokeAsync<IReadOnlyList<TerminalSession>>(
                nameof(IAgentHub.GetSessionsAsync),
                cancellationToken);

            foreach (var session in sessions)
            {
                AnnotateSession(session, machine);
                if (string.Equals(session.Id, sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }
        }

        return null;
    }

    private static HttpRequestMessage CreateRunnerRequest(RunnerEntry entry, HttpMethod method, string relativePath, string actorId)
    {
        var request = new HttpRequestMessage(method, relativePath);
        request.Headers.TryAddWithoutValidation(AgentDeckHeaderNames.Actor, string.IsNullOrWhiteSpace(actorId) ? "coordinator" : actorId.Trim());
        request.Headers.TryAddWithoutValidation("User-Agent", "AgentDeck.Coordinator");
        return request;
    }
}

internal static class HttpRequestMessageJsonExtensions
{
    public static HttpRequestMessage WithJsonContent<T>(this HttpRequestMessage request, T value)
    {
        request.Content = JsonContent.Create(value);
        return request;
    }

    public static Task<HttpResponseMessage> SendAsync(this HttpRequestMessage request, HttpClient httpClient, CancellationToken cancellationToken) =>
        httpClient.SendAsync(request, cancellationToken);
}
