using AgentDeck.Core.Services;

namespace AgentDeck.Services;

/// <summary>MAUI implementation of IAppDataDirectory using FileSystem.AppDataDirectory.</summary>
public sealed class MauiAppDataDirectory : IAppDataDirectory
{
    public string Path => FileSystem.AppDataDirectory;
}
