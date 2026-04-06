using AgentDeck.Core.Models;
using AgentDeck.Shared.Models;

namespace AgentDeck.Core.Services;

/// <inheritdoc />
public sealed class RunnerConnectionManager : IRunnerConnectionManager, IAsyncDisposable
{
    private sealed class MachineEntry
    {
        public required RunnerMachineSettings Machine { get; set; }
        public required IAgentDeckClient Client { get; init; }
    }

    private readonly Lock _lock = new();
    private readonly IAgentDeckClientFactory _clientFactory;
    private readonly Dictionary<string, MachineEntry> _machines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sessionMachineMap = new(StringComparer.OrdinalIgnoreCase);

    public RunnerConnectionManager(IAgentDeckClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    public event EventHandler<RunnerMachineConnectionChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<TerminalOutput>? OutputReceived;
    public event EventHandler<TerminalSession>? SessionCreated;
    public event EventHandler<TerminalSession>? SessionUpdated;
    public event EventHandler<string>? SessionClosed;

    public int ConnectedMachineCount
    {
        get
        {
            lock (_lock)
            {
                return _machines.Values.Count(entry => entry.Client.ConnectionState == HubConnectionState.Connected);
            }
        }
    }

    public HubConnectionState GetConnectionState(string machineId)
    {
        lock (_lock)
        {
            return _machines.TryGetValue(machineId, out var entry)
                ? entry.Client.ConnectionState
                : HubConnectionState.Disconnected;
        }
    }

    public async Task ConnectAsync(RunnerMachineSettings machine, CancellationToken cancellationToken = default)
    {
        var entry = GetOrCreateEntry(machine);
        await entry.Client.ConnectAsync(BuildHubUrl(machine.RunnerUrl), cancellationToken);
    }

    public async Task DisconnectAsync(string machineId)
    {
        var entry = TryGetEntry(machineId);
        if (entry is null)
        {
            return;
        }

        await entry.Client.DisconnectAsync();
        lock (_lock)
        {
            var orphanedSessions = _sessionMachineMap
                .Where(pair => string.Equals(pair.Value, machineId, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToArray();

            foreach (var sessionId in orphanedSessions)
            {
                _sessionMachineMap.Remove(sessionId);
            }
        }
    }

    public async Task JoinSessionAsync(string sessionId)
    {
        var entry = RequireSessionEntry(sessionId);
        await entry.Client.JoinSessionAsync(sessionId);
    }

    public async Task LeaveSessionAsync(string sessionId)
    {
        var entry = RequireSessionEntry(sessionId);
        await entry.Client.LeaveSessionAsync(sessionId);
    }

    public async Task SendInputAsync(string sessionId, string data)
    {
        var entry = RequireSessionEntry(sessionId);
        await entry.Client.SendInputAsync(sessionId, data);
    }

    public async Task ResizeTerminalAsync(string sessionId, int cols, int rows)
    {
        var entry = RequireSessionEntry(sessionId);
        await entry.Client.ResizeTerminalAsync(sessionId, cols, rows);
    }

    public async Task<TerminalSession?> CreateSessionAsync(RunnerMachineSettings machine, CreateTerminalRequest request, CancellationToken cancellationToken = default)
    {
        var entry = GetOrCreateEntry(machine);
        if (entry.Client.ConnectionState != HubConnectionState.Connected)
        {
            await entry.Client.ConnectAsync(BuildHubUrl(machine.RunnerUrl), cancellationToken);
        }

        var session = await entry.Client.CreateSessionAsync(request);
        if (session is null)
        {
            return null;
        }

        return AnnotateSession(session, entry.Machine);
    }

    public async Task CloseSessionAsync(string sessionId)
    {
        var entry = RequireSessionEntry(sessionId);
        await entry.Client.CloseSessionAsync(sessionId);
    }

    public async Task<IReadOnlyList<TerminalSession>> GetSessionsAsync(RunnerMachineSettings machine, CancellationToken cancellationToken = default)
    {
        var entry = GetOrCreateEntry(machine);
        if (entry.Client.ConnectionState != HubConnectionState.Connected)
        {
            await entry.Client.ConnectAsync(BuildHubUrl(machine.RunnerUrl), cancellationToken);
        }

        var sessions = await entry.Client.GetSessionsAsync();
        return sessions.Select(session => AnnotateSession(session, entry.Machine)).ToArray();
    }

    public async Task<WorkspaceInfo?> GetWorkspaceAsync(RunnerMachineSettings machine, CancellationToken cancellationToken = default)
    {
        var entry = GetOrCreateEntry(machine);
        if (entry.Client.ConnectionState != HubConnectionState.Connected)
        {
            await entry.Client.ConnectAsync(BuildHubUrl(machine.RunnerUrl), cancellationToken);
        }

        return await entry.Client.GetWorkspaceAsync(cancellationToken);
    }

    public async Task<MachineCapabilitiesSnapshot?> GetMachineCapabilitiesAsync(RunnerMachineSettings machine, CancellationToken cancellationToken = default)
    {
        var entry = GetOrCreateEntry(machine);
        if (entry.Client.ConnectionState != HubConnectionState.Connected)
        {
            await entry.Client.ConnectAsync(BuildHubUrl(machine.RunnerUrl), cancellationToken);
        }

        return await entry.Client.GetMachineCapabilitiesAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        IAgentDeckClient[] clients;
        lock (_lock)
        {
            clients = _machines.Values.Select(entry => entry.Client).ToArray();
            _machines.Clear();
            _sessionMachineMap.Clear();
        }

        foreach (var client in clients.OfType<IAsyncDisposable>())
        {
            await client.DisposeAsync();
        }
    }

    private MachineEntry GetOrCreateEntry(RunnerMachineSettings machine)
    {
        lock (_lock)
        {
            if (_machines.TryGetValue(machine.Id, out var existing))
            {
                existing.Machine = machine.Clone();
                return existing;
            }

            var entry = new MachineEntry
            {
                Machine = machine.Clone(),
                Client = _clientFactory.Create()
            };

            entry.Client.OutputReceived += (_, output) => OutputReceived?.Invoke(this, output);
            entry.Client.SessionCreated += (_, session) =>
            {
                var annotated = AnnotateSession(session, entry.Machine);
                SessionCreated?.Invoke(this, annotated);
            };
            entry.Client.SessionUpdated += (_, session) =>
            {
                var annotated = AnnotateSession(session, entry.Machine);
                SessionUpdated?.Invoke(this, annotated);
            };
            entry.Client.SessionClosed += (_, sessionId) =>
            {
                lock (_lock)
                {
                    _sessionMachineMap.Remove(sessionId);
                }

                SessionClosed?.Invoke(this, sessionId);
            };
            entry.Client.ConnectionStateChanged += (_, state) =>
            {
                ConnectionStateChanged?.Invoke(this, new RunnerMachineConnectionChangedEventArgs
                {
                    MachineId = entry.Machine.Id,
                    MachineName = entry.Machine.Name,
                    State = state
                });
            };

            _machines[entry.Machine.Id] = entry;
            return entry;
        }
    }

    private MachineEntry? TryGetEntry(string machineId)
    {
        lock (_lock)
        {
            return _machines.TryGetValue(machineId, out var entry) ? entry : null;
        }
    }

    private MachineEntry RequireSessionEntry(string sessionId)
    {
        lock (_lock)
        {
            if (_sessionMachineMap.TryGetValue(sessionId, out var machineId) &&
                _machines.TryGetValue(machineId, out var entry))
            {
                return entry;
            }
        }

        throw new InvalidOperationException($"No connected runner machine owns session '{sessionId}'.");
    }

    private TerminalSession AnnotateSession(TerminalSession session, RunnerMachineSettings machine)
    {
        session.MachineId = machine.Id;
        session.MachineName = machine.Name;

        lock (_lock)
        {
            _sessionMachineMap[session.Id] = machine.Id;
        }

        return session;
    }

    private static string BuildHubUrl(string runnerUrl)
    {
        return $"{runnerUrl.TrimEnd('/')}/hubs/agent";
    }
}
