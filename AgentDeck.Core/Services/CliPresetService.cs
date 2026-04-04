using AgentDeck.Core.Models;

namespace AgentDeck.Core.Services;

/// <inheritdoc />
public sealed class CliPresetService : ICliPresetService
{
    private static readonly IReadOnlyList<CliPreset> _presets =
    [
        new("GitHub Copilot", "copilot", [], "GitHub Copilot CLI agent", "🤖"),
        new("PowerShell",     "pwsh", [],                                           "PowerShell 7",              "🔷"),
        new("Bash",           "bash", [],                                           "Bash shell",                "🐚"),
        new("Command Prompt", "cmd",  ["/k"],                                       "Windows Command Prompt",    "⬛"),
        new("Custom",         "",     [],                                           "Enter command manually",    "✏️"),
    ];

    public IReadOnlyList<CliPreset> GetPresets() => _presets;
}
