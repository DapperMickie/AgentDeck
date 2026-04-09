namespace AgentDeck.Shared.Enums;

/// <summary>Current staging state of a coordinator-assigned runner update.</summary>
public enum RunnerUpdateStageState
{
    None,
    ManifestStaged,
    PayloadStaged,
    Failed
}
