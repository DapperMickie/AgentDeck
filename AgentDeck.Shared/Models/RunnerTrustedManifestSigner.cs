namespace AgentDeck.Shared.Models;

/// <summary>Public verifier metadata for a trusted update manifest signer.</summary>
public sealed class RunnerTrustedManifestSigner
{
    public required string SignerId { get; init; }
    public required string PublicKeyPem { get; init; }
}
