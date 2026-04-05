namespace AgentDeck.Core.Models;

/// <summary>Describes a persistent container mount used by a workload.</summary>
public sealed class WorkloadMount
{
    public required string Name { get; set; }
    public required string TargetPath { get; set; }
    public string Description { get; set; } = string.Empty;
}
