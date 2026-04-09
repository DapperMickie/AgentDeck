namespace AgentDeck.Shared.Models;

/// <summary>Captures the VS Code/debug assets materialized for a session.</summary>
public sealed class VsCodeDebugWorkspaceAssets
{
    public string LaunchConfigurationName { get; init; } = string.Empty;
    public string PreLaunchTaskName { get; init; } = string.Empty;
    public string LaunchSettingsProfileName { get; init; } = string.Empty;
}
