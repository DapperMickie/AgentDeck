namespace AgentDeck.Shared.Models;

/// <summary>Repository metadata for an orchestrated project.</summary>
public sealed class ProjectRepositoryReference
{
    public string Name { get; init; } = string.Empty;
    public string? Owner { get; init; }
    public string? Url { get; init; }
    public string DefaultBranch { get; init; } = "main";
}
