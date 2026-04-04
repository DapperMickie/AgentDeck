using AgentDeck.Core.Models;

namespace AgentDeck.Core.Services;

/// <summary>Provides built-in CLI presets for launching terminal sessions.</summary>
public interface ICliPresetService
{
    IReadOnlyList<CliPreset> GetPresets();
}
