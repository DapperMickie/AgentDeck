using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Runner-reported status of an assigned update manifest.</summary>
public sealed class RunnerUpdateStatus
{
    public RunnerUpdateStageState State { get; init; } = RunnerUpdateStageState.None;
    public string? ManifestId { get; init; }
    public string? ManifestVersion { get; init; }
    public string? StagingDirectory { get; init; }
    public string? StagedArtifactPath { get; init; }
    public DateTimeOffset? StagedAt { get; init; }
    public string? FailureMessage { get; init; }
}
