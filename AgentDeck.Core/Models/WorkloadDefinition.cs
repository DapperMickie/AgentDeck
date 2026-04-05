namespace AgentDeck.Core.Models;

/// <summary>Describes a container workload for the runner and companion app.</summary>
public sealed class WorkloadDefinition
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = string.Empty;
    public string BaseImage { get; set; } = "agentdeck-runner:base";
    public string WorkspaceMountPath { get; set; } = "/workspace";
    public string HomeDirectory { get; set; } = "/agent-home";
    public List<string> SupportedCliCommands { get; set; } = [];
    public Dictionary<string, string> SdkVersions { get; set; } = [];
    public WorkloadCliInstallers CliInstallers { get; set; } = new();
    public Dictionary<string, string> EnvironmentVariables { get; set; } = [];
    public List<WorkloadMount> AuthMounts { get; set; } = [];
    public List<WorkloadMount> CacheMounts { get; set; } = [];
    public List<WorkloadSecret> Secrets { get; set; } = [];
    public List<string> BootstrapCommands { get; set; } = [];
    public bool IsBuiltIn { get; set; }
}
