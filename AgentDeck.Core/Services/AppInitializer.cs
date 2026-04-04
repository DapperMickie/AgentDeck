using AgentDeck.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AgentDeck.Core.Services;

/// <summary>
/// Bootstraps the companion app: wires SignalR client events to the session state service
/// and auto-connects if configured.
/// </summary>
public sealed class AppInitializer
{
    private readonly IAgentDeckClient _client;
    private readonly ISessionStateService _sessionState;
    private readonly IConnectionSettingsService _settingsService;
    private readonly IToastService _toast;
    private readonly ILogger<AppInitializer> _logger;

    public AppInitializer(
        IAgentDeckClient client,
        ISessionStateService sessionState,
        IConnectionSettingsService settingsService,
        IToastService toast,
        ILogger<AppInitializer> logger)
    {
        _client = client;
        _sessionState = sessionState;
        _settingsService = settingsService;
        _toast = toast;
        _logger = logger;

        // Wire hub events → session state
        _client.SessionCreated += (_, s) =>
        {
            _sessionState.AddOrUpdate(s);
            _toast.Show($"Session '{s.Name}' started", ToastKind.Success);
        };
        _client.SessionUpdated += (_, s) => _sessionState.AddOrUpdate(s);
        _client.SessionClosed  += (_, id) =>
        {
            var name = _sessionState.Sessions.FirstOrDefault(s => s.Id == id)?.Name ?? id;
            _sessionState.Remove(id);
            _toast.Show($"Session '{name}' closed", ToastKind.Info);
        };

        _client.ConnectionStateChanged += OnConnectionStateChanged;
    }

    /// <summary>Call once at app startup to auto-connect if configured.</summary>
    public async Task InitializeAsync()
    {
        var settings = await _settingsService.LoadAsync();
        if (!settings.AutoConnect) return;

        var hubUrl = $"{settings.RunnerUrl.TrimEnd('/')}/hubs/agent";
        try
        {
            await _client.ConnectAsync(hubUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-connect to {HubUrl} failed — will retry on Settings page", hubUrl);
        }
    }

    private async void OnConnectionStateChanged(object? sender, HubConnectionState state)
    {
        if (state != HubConnectionState.Connected) return;

        // Sync session list after (re)connect
        try
        {
            var sessions = await _client.GetSessionsAsync();
            _sessionState.Sync(sessions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync sessions after connect");
        }
    }
}
