namespace AgentDeck.Core.Models;

/// <summary>Describes a runtime secret that may be injected for a workload.</summary>
public sealed class WorkloadSecret
{
    public required string Name { get; set; }
    public required string EnvironmentVariable { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
}
