namespace AgentDeck.Shared.Enums;

/// <summary>Compatibility state between the runner's local workflow catalog and the coordinator's desired catalog version.</summary>
public enum RunnerWorkflowCatalogState
{
    Unknown,
    Matched,
    Mismatched
}
