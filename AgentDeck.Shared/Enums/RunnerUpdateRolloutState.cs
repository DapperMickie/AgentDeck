namespace AgentDeck.Shared.Enums;

/// <summary>Coordinator-computed rollout state for a runner update.</summary>
public enum RunnerUpdateRolloutState
{
    UpToDate,
    UpdateAvailable,
    ManifestStaged,
    PayloadStaged,
    ReadyToApply,
    Applying,
    Applied,
    Failed,
    Blocked
}
