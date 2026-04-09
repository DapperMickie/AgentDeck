using AgentDeck.Core.Models;
using AgentDeck.Shared.Models;

namespace AgentDeck.Core.Services;

/// <inheritdoc />
public sealed class RunnerConnectionManager : IRunnerConnectionManager, IAsyncDisposable
{
    private readonly Lock _lock = new();
    private readonly IAgentDeckClient _client;
    private readonly IConnectionSettingsService _settingsService;
    private readonly Dictionary<string, RunnerMachineSettings> _machines = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeMachineIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sessionMachineMap = new(StringComparer.OrdinalIgnoreCase);

    public RunnerConnectionManager(
        IAgentDeckClient client,
        IConnectionSettingsService settingsService)
    {
        _client = client;
        _settingsService = settingsService;
        _client.OutputReceived += (_, output) => OutputReceived?.Invoke(this, output);
        _client.SessionCreated += (_, session) =>
        {
            var annotated = AnnotateSession(session);
            SessionCreated?.Invoke(this, annotated);
        };
        _client.SessionUpdated += (_, session) =>
        {
            var annotated = AnnotateSession(session);
            SessionUpdated?.Invoke(this, annotated);
        };
        _client.SessionClosed += (_, sessionId) =>
        {
            lock (_lock)
            {
                _sessionMachineMap.Remove(sessionId);
            }

            SessionClosed?.Invoke(this, sessionId);
        };
        _client.ConnectionStateChanged += (_, state) =>
        {
            string[] machineIds;
            lock (_lock)
            {
                machineIds = _activeMachineIds.ToArray();
            }

            foreach (var machineId in machineIds)
            {
                RunnerMachineSettings? machine;
                lock (_lock)
                {
                    _machines.TryGetValue(machineId, out machine);
                }

                ConnectionStateChanged?.Invoke(this, new RunnerMachineConnectionChangedEventArgs
                {
                    MachineId = machineId,
                    MachineName = machine?.Name ?? machineId,
                    State = state
                });
            }
        };
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
                return _client.ConnectionState == HubConnectionState.Connected
                    ? _activeMachineIds.Count(id => _machines.ContainsKey(id))
                    : 0;
            }
        }
    }

    public HubConnectionState GetConnectionState(string machineId)
    {
        lock (_lock)
        {
            return _activeMachineIds.Contains(machineId)
                ? _client.ConnectionState
                : HubConnectionState.Disconnected;
        }
    }

    public async Task ConnectAsync(RunnerMachineSettings machine, CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.LoadAsync();
        if (string.IsNullOrWhiteSpace(settings.CoordinatorUrl))
        {
            throw new InvalidOperationException("Coordinator URL is not configured.");
        }

        await _client.ConnectAsync(settings.CoordinatorUrl, cancellationToken);
        lock (_lock)
        {
            _machines[machine.Id] = machine.Clone();
            _activeMachineIds.Add(machine.Id);
        }

        ConnectionStateChanged?.Invoke(this, new RunnerMachineConnectionChangedEventArgs
        {
            MachineId = machine.Id,
            MachineName = machine.Name,
            State = _client.ConnectionState
        });
    }

    public async Task DisconnectAsync(string machineId)
    {
        var shouldDisconnectCoordinator = false;
        RunnerMachineSettings? machine;
        lock (_lock)
        {
            _machines.TryGetValue(machineId, out machine);
            _activeMachineIds.Remove(machineId);
            var orphanedSessions = _sessionMachineMap
                .Where(pair => string.Equals(pair.Value, machineId, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToArray();

            foreach (var sessionId in orphanedSessions)
            {
                _sessionMachineMap.Remove(sessionId);
            }

            shouldDisconnectCoordinator = _activeMachineIds.Count == 0;
        }

        if (shouldDisconnectCoordinator)
        {
            await _client.DisconnectAsync();
        }

        ConnectionStateChanged?.Invoke(this, new RunnerMachineConnectionChangedEventArgs
        {
            MachineId = machineId,
            MachineName = machine?.Name ?? machineId,
            State = HubConnectionState.Disconnected
        });
    }

    public async Task JoinSessionAsync(string sessionId)
    {
        RequireSessionMachineId(sessionId);
        await _client.JoinSessionAsync(sessionId);
    }

    public async Task LeaveSessionAsync(string sessionId)
    {
        RequireSessionMachineId(sessionId);
        await _client.LeaveSessionAsync(sessionId);
    }

    public async Task SendInputAsync(string sessionId, string data)
    {
        RequireSessionMachineId(sessionId);
        await _client.SendInputAsync(sessionId, data);
    }

    public async Task ResizeTerminalAsync(string sessionId, int cols, int rows)
    {
        RequireSessionMachineId(sessionId);
        await _client.ResizeTerminalAsync(sessionId, cols, rows);
    }

    public async Task<TerminalSession?> CreateSessionAsync(RunnerMachineSettings machine, CreateTerminalRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureMachineConnectedAsync(machine, cancellationToken);
        var session = await _client.CreateSessionAsync(machine.Id, request);
        if (session is null)
        {
            return null;
        }

        return AnnotateSession(session, machine);
    }

    public async Task CloseSessionAsync(string sessionId)
    {
        RequireSessionMachineId(sessionId);
        await _client.CloseSessionAsync(sessionId);
    }

    public async Task<IReadOnlyList<TerminalSession>> GetSessionsAsync(RunnerMachineSettings machine, CancellationToken cancellationToken = default)
    {
        await EnsureMachineConnectedAsync(machine, cancellationToken);
        var sessions = await _client.GetSessionsAsync(machine.Id);
        return sessions.Select(session => AnnotateSession(session, machine)).ToArray();
    }

    public async Task<WorkspaceInfo?> GetWorkspaceAsync(RunnerMachineSettings machine, CancellationToken cancellationToken = default)
    {
        await EnsureMachineConnectedAsync(machine, cancellationToken);
        return await _client.GetWorkspaceAsync(machine.Id, cancellationToken);
    }

    public async Task<MachineCapabilitiesSnapshot?> GetMachineCapabilitiesAsync(RunnerMachineSettings machine, CancellationToken cancellationToken = default)
    {
        await EnsureMachineConnectedAsync(machine, cancellationToken);
        return await _client.GetMachineCapabilitiesAsync(machine.Id, cancellationToken);
    }

    public async Task<MachineCapabilityInstallResult?> InstallMachineCapabilityAsync(RunnerMachineSettings machine, string capabilityId, string? version = null, CancellationToken cancellationToken = default)
    {
        await EnsureMachineConnectedAsync(machine, cancellationToken);
        return await _client.InstallMachineCapabilityAsync(machine.Id, capabilityId, version, cancellationToken);
    }

    public async Task<MachineCapabilityInstallResult?> UpdateMachineCapabilityAsync(RunnerMachineSettings machine, string capabilityId, CancellationToken cancellationToken = default)
    {
        await EnsureMachineConnectedAsync(machine, cancellationToken);
        return await _client.UpdateMachineCapabilityAsync(machine.Id, capabilityId, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            _machines.Clear();
            _activeMachineIds.Clear();
            _sessionMachineMap.Clear();
        }

        if (_client is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
    }

    private async Task EnsureMachineConnectedAsync(RunnerMachineSettings machine, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _machines[machine.Id] = machine.Clone();
        }

        if (GetConnectionState(machine.Id) != HubConnectionState.Connected)
        {
            await ConnectAsync(machine, cancellationToken);
        }
    }

    private string RequireSessionMachineId(string sessionId)
    {
        lock (_lock)
        {
            if (_sessionMachineMap.TryGetValue(sessionId, out var machineId))
            {
                return machineId;
            }
        }

        throw new InvalidOperationException($"No connected runner machine owns session '{sessionId}'.");
    }

    private TerminalSession AnnotateSession(TerminalSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.MachineId))
        {
            lock (_lock)
            {
                _sessionMachineMap[session.Id] = session.MachineId;
                if (_machines.TryGetValue(session.MachineId, out var machine))
                {
                    session.MachineName ??= machine.Name;
                }
            }
        }

        return session;
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
}
