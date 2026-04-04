namespace AgentDeck.Core.Services;

/// <summary>Platform abstraction for the app's writable data directory.</summary>
public interface IAppDataDirectory
{
    string Path { get; }
}
