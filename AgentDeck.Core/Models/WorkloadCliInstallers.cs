namespace AgentDeck.Core.Models;

/// <summary>Optional installer hints for workload tooling.</summary>
public sealed class WorkloadCliInstallers
{
    public List<string> AptPackages { get; set; } = [];
    public List<string> NpmGlobalPackages { get; set; } = [];
    public List<string> PipxPackages { get; set; } = [];
    public List<string> DotNetTools { get; set; } = [];
}
