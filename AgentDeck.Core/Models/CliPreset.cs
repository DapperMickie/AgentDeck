namespace AgentDeck.Core.Models;

/// <summary>Describes a CLI tool preset for launching a terminal session.</summary>
public sealed record CliPreset(
    string Name,
    string Command,
    IReadOnlyList<string> Args,
    string Description,
    string Icon
);
