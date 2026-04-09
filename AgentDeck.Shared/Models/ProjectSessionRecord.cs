using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

public sealed class ProjectSessionRecord
{
    public string Id { get; init; } = string.Empty;
    public string ProjectId { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public string? MachineId { get; init; }
    public string? MachineName { get; init; }
    public string? CompanionId { get; init; }
    public DateTimeOffset ControlUpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<string> AttachedCompanionIds { get; init; } = [];
    public IReadOnlyList<string> ViewerCompanionIds { get; init; } = [];
    public ProjectSessionStatus Status { get; init; } = ProjectSessionStatus.Open;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<ProjectSessionSurface> Surfaces { get; init; } = [];
}
