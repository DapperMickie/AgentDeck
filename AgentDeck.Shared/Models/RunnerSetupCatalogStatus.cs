using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

public sealed class RunnerSetupCatalogStatus
{
    public RunnerSetupCatalogState State { get; init; } = RunnerSetupCatalogState.Unknown;
    public string? CatalogId { get; init; }
    public string? LocalCatalogVersion { get; init; }
    public string? DesiredCatalogVersion { get; init; }
    public string? StatusMessage { get; init; }
}
