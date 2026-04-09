namespace AgentDeck.Shared.Models;

/// <summary>Describes the detached signature for a runner update manifest.</summary>
public sealed class RunnerUpdateManifestSignature
{
    public string Algorithm { get; init; } = "RSA-SHA256";
    public required string SignerId { get; init; }
    public required string Value { get; init; }
}
