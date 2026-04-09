using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Represents a runner-hosted VS Code debug session distinct from jobs and viewer records.</summary>
public sealed class VsCodeDebugSession
{
    public string Id { get; init; } = string.Empty;
    public string OrchestrationSessionId { get; init; } = string.Empty;
    public string JobId { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public ApplicationTargetPlatform Platform { get; init; }
    public string? MachineId { get; init; }
    public string? MachineName { get; init; }
    public string WorkspaceDirectory { get; init; } = string.Empty;
    public string StartupProjectPath { get; init; } = string.Empty;
    public string DebugConfigurationName { get; init; } = string.Empty;
    public string? ViewerSessionId { get; init; }
    public string? TargetDisplayName { get; init; }
    public VsCodeDebugSessionStatus Status { get; init; } = VsCodeDebugSessionStatus.Requested;
    public VsCodeDebuggerVisibilityState DebuggerVisibility { get; init; } = VsCodeDebuggerVisibilityState.Unknown;
    public string? StatusMessage { get; init; }
    public IReadOnlyList<VsCodeDebugExtensionRequirement> RequiredExtensions { get; init; } = [];
    public VsCodeDebugWorkspaceAssets WorkspaceAssets { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
