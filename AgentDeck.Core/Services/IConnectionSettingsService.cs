using AgentDeck.Core.Models;

namespace AgentDeck.Core.Services;

/// <summary>Persists and retrieves runner connection settings.</summary>
public interface IConnectionSettingsService
{
    Task<ConnectionSettings> LoadAsync();
    Task SaveAsync(ConnectionSettings settings);
}
