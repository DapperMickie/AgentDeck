using System.Text.Json;
using AgentDeck.Core.Models;

namespace AgentDeck.Core.Services;

/// <inheritdoc />
public sealed class ConnectionSettingsService : IConnectionSettingsService
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public ConnectionSettingsService(IAppDataDirectory appData)
    {
        _filePath = System.IO.Path.Combine(appData.Path, "connection-settings.json");
    }

    public async Task<ConnectionSettings> LoadAsync()
    {
        if (!File.Exists(_filePath))
            return new ConnectionSettings();

        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<ConnectionSettings>(stream) ?? new ConnectionSettings();
        }
        catch
        {
            return new ConnectionSettings();
        }
    }

    public async Task SaveAsync(ConnectionSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_filePath)!);
        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, settings, _json);
    }
}
