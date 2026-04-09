namespace AgentDeck.Shared.Models;

/// <summary>Describes a VS Code extension needed for a debug session.</summary>
public sealed class VsCodeDebugExtensionRequirement
{
    public string ExtensionId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsInstalled { get; init; }
}
