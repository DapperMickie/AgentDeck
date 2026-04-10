using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

public sealed class RunnerSetupCatalog
{
    public required string CatalogId { get; init; }
    public required string Version { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<RunnerSetupCapabilityDefinition> Capabilities { get; init; } = [];
}

public sealed class RunnerSetupCapabilityDefinition
{
    public required string CapabilityId { get; init; }
    public required string DisplayName { get; init; }
    public IReadOnlyList<RunnerSetupActionDefinition> Actions { get; init; } = [];
}

public sealed class RunnerSetupActionDefinition
{
    public required string Action { get; init; }
    public IReadOnlyList<RunnerSetupRecipe> Recipes { get; init; } = [];
}

public sealed class RunnerSetupRecipe
{
    public RunnerHostPlatform Platform { get; init; } = RunnerHostPlatform.Unknown;
    public RunnerSetupRecipeExecutionKind ExecutionKind { get; init; } = RunnerSetupRecipeExecutionKind.PlatformShell;
    public string? MatchVersionPattern { get; init; }
    public bool MatchWhenVersionMissing { get; init; }
    public string? DefaultVersion { get; init; }
    public string? CommandText { get; init; }
    public string? FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public IReadOnlyList<string> RequiredCommands { get; init; } = [];
    public string? RequiredCommandsMessage { get; init; }
}
