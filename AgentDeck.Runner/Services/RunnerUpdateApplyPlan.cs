namespace AgentDeck.Runner.Services;

internal sealed class RunnerUpdateApplyPlan
{
    public required int TargetProcessId { get; init; }
    public required string ManifestId { get; init; }
    public required string ManifestVersion { get; init; }
    public required string StagingDirectory { get; init; }
    public required string StatusPath { get; init; }
    public required string ArtifactPath { get; init; }
    public required string SourceInstallDirectory { get; init; }
    public required string CandidateInstallDirectory { get; init; }
    public TimeSpan ProcessExitTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public DateTimeOffset ApplyStartedAt { get; init; } = DateTimeOffset.UtcNow;
}
