namespace AgentDeck.Shared.Models;

/// <summary>Maps a project to a concrete workspace path on a specific machine.</summary>
public sealed class ProjectWorkspaceMapping
{
    public string MachineId { get; init; } = string.Empty;
    public string? MachineName { get; init; }
    public string ProjectPath { get; init; } = string.Empty;
    public bool IsPrimary { get; init; }
}
