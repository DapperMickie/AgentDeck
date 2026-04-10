using AgentDeck.Shared.Enums;

namespace AgentDeck.Coordinator.Configuration;

public sealed class CoordinatorSetupCatalogOptions
{
    public string CatalogId { get; set; } = "default-setup";
    public string Version { get; set; } = "1";
    public string DisplayName { get; set; } = "Default Setup Catalog";
    public string? Description { get; set; }
    public IReadOnlyList<CoordinatorSetupCapabilityOptions> Capabilities { get; set; } = [];
}

public sealed class CoordinatorSetupCapabilityOptions
{
    public string CapabilityId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public IReadOnlyList<CoordinatorSetupActionOptions> Actions { get; set; } = [];
}

public sealed class CoordinatorSetupActionOptions
{
    public string Action { get; set; } = "install";
    public IReadOnlyList<CoordinatorSetupRecipeOptions> Recipes { get; set; } = [];
}

public sealed class CoordinatorSetupRecipeOptions
{
    public RunnerHostPlatform Platform { get; set; } = RunnerHostPlatform.Unknown;
    public RunnerSetupRecipeExecutionKind ExecutionKind { get; set; } = RunnerSetupRecipeExecutionKind.PlatformShell;
    public string? MatchVersionPattern { get; set; }
    public bool MatchWhenVersionMissing { get; set; }
    public string? DefaultVersion { get; set; }
    public string? CommandText { get; set; }
    public string? FileName { get; set; }
    public IReadOnlyList<string> Arguments { get; set; } = [];
    public IReadOnlyList<string> RequiredCommands { get; set; } = [];
    public string? RequiredCommandsMessage { get; set; }
}
