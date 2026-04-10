using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Runner-reported status of workflow catalog compatibility with the coordinator.</summary>
public sealed class RunnerWorkflowCatalogStatus
{
    public RunnerWorkflowCatalogState State { get; init; } = RunnerWorkflowCatalogState.Unknown;
    public string? LocalCatalogVersion { get; init; }
    public string? DesiredCatalogVersion { get; init; }
    public string? StatusMessage { get; init; }
}
