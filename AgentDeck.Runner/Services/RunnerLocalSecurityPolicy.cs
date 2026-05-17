using AgentDeck.Runner.Configuration;
using AgentDeck.Shared.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AgentDeck.Runner.Services;

public delegate bool RunnerDefinitionSignatureVerifier(string publicKeyPem, out string error);

public sealed class RunnerLocalSecurityPolicy
{
    private readonly WorkerCoordinatorOptions _coordinatorOptions;
    private readonly RunnerLocalSecurityPolicyOptions _localOptions;
    private readonly IHostEnvironment _environment;
    private IReadOnlyList<RunnerTrustedManifestSigner>? _trustedSigners;

    public RunnerLocalSecurityPolicy(
        IOptions<WorkerCoordinatorOptions> coordinatorOptions,
        IOptions<RunnerLocalSecurityPolicyOptions> localOptions,
        IHostEnvironment environment)
    {
        _coordinatorOptions = coordinatorOptions.Value;
        _localOptions = localOptions.Value;
        _environment = environment;
    }

    public RunnerControlPlaneSecurityPolicy EnforceMinimums(RunnerControlPlaneSecurityPolicy coordinatorPolicy)
    {
        ArgumentNullException.ThrowIfNull(coordinatorPolicy);

        var localSignerIds = GetAllowedLocalSignerIds();
        var coordinatorSignerIds = coordinatorPolicy.TrustedManifestSignerIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var effectiveSignerIds = localSignerIds.Count == 0
            ? coordinatorSignerIds.ToArray()
            : coordinatorSignerIds.Count == 0
                ? localSignerIds.ToArray()
                : localSignerIds.Where(coordinatorSignerIds.Contains).ToArray();

        var requiresSignedDefinitions = _localOptions.RequireSignedWorkflowPacks ||
            _localOptions.RequireSignedCapabilityCatalogs ||
            _localOptions.RequireSignedSetupCatalogs;

        if ((_localOptions.RequireSignedUpdateManifest || requiresSignedDefinitions) && effectiveSignerIds.Length == 0)
        {
            throw new InvalidOperationException("Runner local security policy requires signed coordinator definitions but no trusted signer ids are available.");
        }

        return new RunnerControlPlaneSecurityPolicy
        {
            PolicyVersion = coordinatorPolicy.PolicyVersion,
            AllowUpdateStaging = coordinatorPolicy.AllowUpdateStaging,
            RequireCoordinatorOriginForArtifacts = _localOptions.RequireCoordinatorOriginForArtifacts || coordinatorPolicy.RequireCoordinatorOriginForArtifacts,
            RequireUpdateArtifactChecksum = _localOptions.RequireUpdateArtifactChecksum || coordinatorPolicy.RequireUpdateArtifactChecksum,
            RequireSignedUpdateManifest = _localOptions.RequireSignedUpdateManifest || coordinatorPolicy.RequireSignedUpdateManifest,
            RequireManifestProvenance = _localOptions.RequireManifestProvenance || coordinatorPolicy.RequireManifestProvenance,
            TrustedManifestSignerIds = effectiveSignerIds,
            AllowWorkflowPackExecution = coordinatorPolicy.AllowWorkflowPackExecution,
            AllowUpdateApply = coordinatorPolicy.AllowUpdateApply
        };
    }

    public IReadOnlyList<RunnerTrustedManifestSigner> GetTrustedSigners() =>
        _trustedSigners ??= LoadTrustedSigners();

    public void VerifySignedDefinition(string payloadName, RunnerUpdateManifestSignature? signature, RunnerUpdateManifestProvenance? provenance, RunnerDefinitionSignatureVerifier verifier)
    {
        if (provenance is null || string.IsNullOrWhiteSpace(provenance.SourceRepository) || string.IsNullOrWhiteSpace(provenance.SourceRevision))
        {
            throw new InvalidOperationException($"Coordinator {payloadName} did not include required provenance.");
        }

        if (signature is null)
        {
            throw new InvalidOperationException($"Coordinator {payloadName} did not include a signature.");
        }

        var allowedSignerIds = GetAllowedLocalSignerIds();
        if (allowedSignerIds.Count > 0 && !allowedSignerIds.Contains(signature.SignerId, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Coordinator {payloadName} signer '{signature.SignerId}' is not trusted by the runner-local security policy.");
        }

        var signer = GetTrustedSigners()
            .FirstOrDefault(candidate => string.Equals(candidate.SignerId, signature.SignerId, StringComparison.OrdinalIgnoreCase));
        if (signer is null)
        {
            throw new InvalidOperationException($"Runner does not have a trusted public key configured for signer '{signature.SignerId}'.");
        }

        if (!verifier(signer.PublicKeyPem, out var error))
        {
            throw new InvalidOperationException(error);
        }
    }

    public bool RequireSignedWorkflowPacks => _localOptions.RequireSignedWorkflowPacks;
    public bool RequireSignedCapabilityCatalogs => _localOptions.RequireSignedCapabilityCatalogs;
    public bool RequireSignedSetupCatalogs => _localOptions.RequireSignedSetupCatalogs;

    private HashSet<string> GetAllowedLocalSignerIds()
    {
        var signerIds = _localOptions.TrustedSignerIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (signerIds.Count == 0)
        {
            foreach (var signer in GetTrustedSigners())
            {
                signerIds.Add(signer.SignerId.Trim());
            }
        }

        if (!_localOptions.AllowDevSignerInProduction && !_environment.IsDevelopment())
        {
            signerIds.Remove("agentdeck-dev");
        }

        return signerIds;
    }

    private IReadOnlyList<RunnerTrustedManifestSigner> LoadTrustedSigners()
    {
        var signers = new Dictionary<string, RunnerTrustedManifestSigner>(StringComparer.OrdinalIgnoreCase);
        foreach (var signer in _coordinatorOptions.TrustedManifestSigners)
        {
            AddSigner(signers, signer.SignerId, signer.PublicKeyPem);
        }

        var signersDirectory = ResolveSignersDirectory();
        if (Directory.Exists(signersDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(signersDirectory, "*.pem", SearchOption.TopDirectoryOnly))
            {
                AddSigner(signers, Path.GetFileNameWithoutExtension(file), File.ReadAllText(file));
            }
        }

        return signers.Values.ToArray();
    }

    private string ResolveSignersDirectory() =>
        string.IsNullOrWhiteSpace(_localOptions.TrustedSignersDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "signers.d")
            : Path.GetFullPath(_localOptions.TrustedSignersDirectory);

    private static void AddSigner(IDictionary<string, RunnerTrustedManifestSigner> signers, string? signerId, string? publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(signerId) || string.IsNullOrWhiteSpace(publicKeyPem))
        {
            return;
        }

        signers[signerId.Trim()] = new RunnerTrustedManifestSigner
        {
            SignerId = signerId.Trim(),
            PublicKeyPem = publicKeyPem.Trim()
        };
    }
}
