using AgentDeck.Shared.Models;

namespace AgentDeck.Coordinator.Configuration;

public abstract class CoordinatorDefinitionSignatureOptions
{
    public string SignatureAlgorithm { get; set; } = RunnerUpdateManifestSigning.RsaSha256Algorithm;
    public string? SignerId { get; set; }
    public string? PrivateKeyPem { get; set; }
    public string? Signature { get; set; }
}
