using System.Text.Json;
using AgentDeck.Core.Models;

namespace AgentDeck.Core.Services;

/// <inheritdoc />
public sealed class ConnectionSettingsService : IConnectionSettingsService
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    private sealed class LegacyConnectionSettings
    {
        public string RunnerUrl { get; set; } = "http://localhost:5000";
        public bool AutoConnect { get; set; } = true;
    }

    public ConnectionSettingsService(IAppDataDirectory appData)
    {
        _filePath = System.IO.Path.Combine(appData.Path, "connection-settings.json");
    }

    public async Task<ConnectionSettings> LoadAsync()
    {
        if (!File.Exists(_filePath))
            return ConnectionSettings.CreateDefault();

        try
        {
            await using var stream = File.OpenRead(_filePath);
            using var document = await JsonDocument.ParseAsync(stream);

            if (document.RootElement.TryGetProperty("Machines", out _))
            {
                var settings = document.Deserialize<ConnectionSettings>(_json) ?? ConnectionSettings.CreateDefault();
                settings.Normalize();
                return settings;
            }

            var legacy = document.Deserialize<LegacyConnectionSettings>(_json);
            return CreateFromLegacy(legacy);
        }
        catch
        {
            return ConnectionSettings.CreateDefault();
        }
    }

    public async Task SaveAsync(ConnectionSettings settings)
    {
        settings.Normalize();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_filePath)!);
        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, settings, _json);
    }

    private static ConnectionSettings CreateFromLegacy(LegacyConnectionSettings? legacy)
    {
        var machine = ConnectionSettings.CreateLocalMachine();

        if (legacy is not null)
        {
            machine.RunnerUrl = string.IsNullOrWhiteSpace(legacy.RunnerUrl) ? machine.RunnerUrl : legacy.RunnerUrl.Trim();
            machine.AutoConnect = legacy.AutoConnect;
        }

        var settings = new ConnectionSettings
        {
            Machines = [machine],
            PreferredMachineId = machine.Id
        };
        settings.Normalize();
        return settings;
    }
}
