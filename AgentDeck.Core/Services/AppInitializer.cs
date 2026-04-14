using AgentDeck.Shared.Models;
using AgentDeck.Core.Models;
using Microsoft.Extensions.Logging;

namespace AgentDeck.Core.Services;

/// <summary>
/// Bootstraps the companion app: wires SignalR client events to the session state service
/// and auto-connects if configured.
/// </summary>
public sealed class AppInitializer
{
    private readonly IRunnerConnectionManager _connections;
    private readonly ISessionStateService _sessionState;
    private readonly IConnectionSettingsService _settingsService;
    private readonly IToastService _toast;
    private readonly ILogger<AppInitializer> _logger;

    public AppInitializer(
        IRunnerConnectionManager connections,
        ISessionStateService sessionState,
        IConnectionSettingsService settingsService,
        IToastService toast,
        ILogger<AppInitializer> logger)
    {
        _connections = connections;
        _sessionState = sessionState;
        _settingsService = settingsService;
        _toast = toast;
        _logger = logger;

        // Wire hub events → session state
        _connections.SessionCreated += (_, s) =>
        {
            _sessionState.AddOrUpdate(s);
            _toast.Show($"Session '{s.Name}' started on {s.MachineName ?? "runner"}", ToastKind.Success);
        };
        _connections.OutputReceived += (_, output) => _sessionState.AppendOutput(output);
        _connections.SessionUpdated += (_, s) => _sessionState.AddOrUpdate(s);
        _connections.SessionClosed += (_, id) =>
        {
            var name = _sessionState.Sessions.FirstOrDefault(s => s.Id == id)?.Name ?? id;
            _sessionState.Remove(id);
            _toast.Show($"Session '{name}' closed", ToastKind.Info);
        };

        _connections.ConnectionStateChanged += OnConnectionStateChanged;
    }

    /// <summary>Call once at app startup to auto-connect if configured.</summary>
    public async Task InitializeAsync()
    {
        var settings = await _settingsService.LoadAsync();
        foreach (var machine in settings.Machines.Where(machine => machine.AutoConnect))
        {
            try
            {
                await _connections.ConnectAsync(machine);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-connect to machine {MachineName} failed — will retry on Settings page", machine.Name);
            }
        }
    }

    private async void OnConnectionStateChanged(object? sender, RunnerMachineConnectionChangedEventArgs e)
    {
        if (e.State != HubConnectionState.Connected)
        {
            return;
        }

        try
        {
            var settings = await _settingsService.LoadAsync();
            var connectedSessions = new List<TerminalSession>();

            foreach (var machine in settings.Machines)
            {
                if (_connections.GetConnectionState(machine.Id) != HubConnectionState.Connected)
                {
                    continue;
                }

                var sessions = await _connections.GetSessionsAsync(machine);
                connectedSessions.AddRange(sessions);
            }

            _sessionState.Sync(connectedSessions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync sessions after machine connect");
        }
    }
}
