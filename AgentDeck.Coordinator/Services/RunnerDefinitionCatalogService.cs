using AgentDeck.Coordinator.Configuration;
using AgentDeck.Shared.Models;
using Microsoft.Extensions.Options;

namespace AgentDeck.Coordinator.Services;

public sealed class RunnerDefinitionCatalogService : IRunnerDefinitionCatalogService
{
    private readonly RunnerUpdateManifest _desiredUpdateManifest;
    private readonly RunnerWorkflowPack _desiredWorkflowPack;

    public RunnerDefinitionCatalogService(IOptions<CoordinatorOptions> coordinatorOptions)
    {
        var options = coordinatorOptions.Value;
        var desiredUpdateManifest = options.DesiredUpdateManifest ?? new CoordinatorUpdateManifestOptions();
        var desiredWorkflowPack = options.DesiredWorkflowPack ?? new CoordinatorWorkflowPackOptions();
        var securityPolicy = options.SecurityPolicy ?? new CoordinatorSecurityPolicyOptions();

        _desiredUpdateManifest = new RunnerUpdateManifest
        {
            ManifestId = NormalizeRequired(desiredUpdateManifest.ManifestId, $"{CoordinatorOptions.SectionName}:DesiredUpdateManifest:ManifestId"),
            Version = NormalizeRequired(desiredUpdateManifest.Version, $"{CoordinatorOptions.SectionName}:DesiredUpdateManifest:Version"),
            Channel = NormalizeRequired(desiredUpdateManifest.Channel, $"{CoordinatorOptions.SectionName}:DesiredUpdateManifest:Channel"),
            ArtifactUrl = NormalizeRequired(desiredUpdateManifest.ArtifactUrl, $"{CoordinatorOptions.SectionName}:DesiredUpdateManifest:ArtifactUrl"),
            Sha256 = NormalizeOptional(desiredUpdateManifest.Sha256),
            ArtifactSizeBytes = desiredUpdateManifest.ArtifactSizeBytes,
            MinimumProtocolVersion = desiredUpdateManifest.MinimumProtocolVersion,
            MaximumProtocolVersion = desiredUpdateManifest.MaximumProtocolVersion,
            PublishedAt = desiredUpdateManifest.PublishedAt ?? DateTimeOffset.UtcNow,
            Notes = NormalizeOptional(desiredUpdateManifest.Notes),
            Provenance = HasConfiguredProvenance(desiredUpdateManifest)
                ? new RunnerUpdateManifestProvenance
                {
                    SourceRepository = NormalizeRequired(desiredUpdateManifest.SourceRepository, $"{CoordinatorOptions.SectionName}:DesiredUpdateManifest:SourceRepository"),
                    SourceRevision = NormalizeRequired(desiredUpdateManifest.SourceRevision, $"{CoordinatorOptions.SectionName}:DesiredUpdateManifest:SourceRevision"),
                    BuildIdentifier = NormalizeOptional(desiredUpdateManifest.BuildIdentifier),
                    PublishedBy = NormalizeOptional(desiredUpdateManifest.PublishedBy),
                    ProvenanceUri = NormalizeOptional(desiredUpdateManifest.ProvenanceUri)
                }
                : null,
            Signature = HasConfiguredSignature(desiredUpdateManifest)
                ? new RunnerUpdateManifestSignature
                {
                    Algorithm = NormalizeRequired(desiredUpdateManifest.SignatureAlgorithm, $"{CoordinatorOptions.SectionName}:DesiredUpdateManifest:SignatureAlgorithm"),
                    SignerId = NormalizeRequired(desiredUpdateManifest.SignerId, $"{CoordinatorOptions.SectionName}:DesiredUpdateManifest:SignerId"),
                    Value = NormalizeRequired(desiredUpdateManifest.Signature, $"{CoordinatorOptions.SectionName}:DesiredUpdateManifest:Signature")
                }
                : null
        };
        ValidateUpdateManifest(_desiredUpdateManifest, options.PublicBaseUrl, securityPolicy);

        _desiredWorkflowPack = new RunnerWorkflowPack
        {
            PackId = NormalizeRequired(desiredWorkflowPack.PackId, $"{CoordinatorOptions.SectionName}:DesiredWorkflowPack:PackId"),
            Version = NormalizeRequired(desiredWorkflowPack.Version, $"{CoordinatorOptions.SectionName}:DesiredWorkflowPack:Version"),
            DisplayName = NormalizeRequired(desiredWorkflowPack.DisplayName, $"{CoordinatorOptions.SectionName}:DesiredWorkflowPack:DisplayName"),
            Description = NormalizeOptional(desiredWorkflowPack.Description),
            Steps = (desiredWorkflowPack.Steps ?? [])
                .Where(step => !string.IsNullOrWhiteSpace(step.StepId))
                .Select(step => new RunnerWorkflowStep
                {
                    StepId = NormalizeRequired(step.StepId, $"{CoordinatorOptions.SectionName}:DesiredWorkflowPack:Steps:StepId"),
                    Kind = step.Kind,
                    DisplayName = NormalizeOptional(step.DisplayName),
                    CommandText = NormalizeOptional(step.CommandText),
                    SourceUri = NormalizeOptional(step.SourceUri),
                    DestinationPath = NormalizeOptional(step.DestinationPath),
                    Inputs = (step.Inputs ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                        .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                        .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                })
                .ToArray()
        };
    }

    public RunnerUpdateManifest GetDesiredUpdateManifest() => _desiredUpdateManifest;

    public RunnerWorkflowPack GetDesiredWorkflowPack() => _desiredWorkflowPack;

    public RunnerUpdateManifest? GetUpdateManifest(string manifestId) =>
        string.Equals(_desiredUpdateManifest.ManifestId, manifestId, StringComparison.OrdinalIgnoreCase)
            ? _desiredUpdateManifest
            : null;

    public RunnerWorkflowPack? GetWorkflowPack(string packId) =>
        string.Equals(_desiredWorkflowPack.PackId, packId, StringComparison.OrdinalIgnoreCase)
            ? _desiredWorkflowPack
            : null;

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeRequired(string? value, string settingName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Coordinator setting '{settingName}' is required.")
            : value.Trim();

    private static void ValidateUpdateManifest(
        RunnerUpdateManifest manifest,
        string? publicBaseUrl,
        CoordinatorSecurityPolicyOptions securityPolicy)
    {
        var trustedSigners = BuildTrustedSignerLookup(securityPolicy.TrustedManifestSigners);

        if (securityPolicy.RequireUpdateArtifactChecksum && string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            throw new InvalidOperationException(
                $"Coordinator setting '{CoordinatorOptions.SectionName}:DesiredUpdateManifest:Sha256' is required when update artifact checksums are enforced.");
        }

        if ((securityPolicy.RequireSignedUpdateManifest || manifest.Signature is not null) && trustedSigners.Count == 0)
        {
            throw new InvalidOperationException(
                $"Coordinator security policy requires at least one trusted manifest signer when signed manifests are enabled.");
        }

        if ((securityPolicy.RequireManifestProvenance || manifest.Provenance is not null) &&
            !RunnerUpdateManifestSigning.HasRequiredProvenance(manifest, out var provenanceError))
        {
            throw new InvalidOperationException(provenanceError);
        }

        if (securityPolicy.RequireSignedUpdateManifest && manifest.Signature is null)
        {
            throw new InvalidOperationException(
                $"Coordinator settings '{CoordinatorOptions.SectionName}:DesiredUpdateManifest:SignerId' and '{CoordinatorOptions.SectionName}:DesiredUpdateManifest:Signature' are required when signed manifests are enforced.");
        }

        if (manifest.Signature is not null)
        {
            if (!trustedSigners.TryGetValue(manifest.Signature.SignerId, out var signer))
            {
                throw new InvalidOperationException(
                    $"Coordinator security policy does not define a trusted manifest signer entry for '{manifest.Signature.SignerId}'.");
            }

            if (!RunnerUpdateManifestSigning.VerifySignature(manifest, signer.PublicKeyPem, out var signatureError))
            {
                throw new InvalidOperationException(signatureError);
            }
        }

        if (!Uri.TryCreate(manifest.ArtifactUrl, UriKind.RelativeOrAbsolute, out var artifactUri))
        {
            throw new InvalidOperationException(
                $"Coordinator setting '{CoordinatorOptions.SectionName}:DesiredUpdateManifest:ArtifactUrl' must be a valid URI.");
        }

        if (!artifactUri.IsAbsoluteUri)
        {
            if (securityPolicy.RequireCoordinatorOriginForArtifacts)
            {
                throw new InvalidOperationException(
                    $"Coordinator setting '{CoordinatorOptions.SectionName}:DesiredUpdateManifest:ArtifactUrl' must be an absolute URI when coordinator-origin artifacts are required.");
            }

            return;
        }

        if (!string.Equals(artifactUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(artifactUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Coordinator update artifact URLs must use HTTP or HTTPS.");
        }

        if (!securityPolicy.RequireCoordinatorOriginForArtifacts)
        {
            return;
        }

        var baseUrl = NormalizeRequired(publicBaseUrl, $"{CoordinatorOptions.SectionName}:PublicBaseUrl");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var publicBaseUri))
        {
            throw new InvalidOperationException(
                $"Coordinator setting '{CoordinatorOptions.SectionName}:PublicBaseUrl' must be an absolute URI when coordinator-origin artifacts are required.");
        }

        var sameOrigin = string.Equals(publicBaseUri.Scheme, artifactUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(publicBaseUri.Host, artifactUri.Host, StringComparison.OrdinalIgnoreCase) &&
            publicBaseUri.Port == artifactUri.Port;
        if (!sameOrigin)
        {
            throw new InvalidOperationException(
                $"Coordinator update artifact '{manifest.ArtifactUrl}' must match coordinator origin '{publicBaseUri.GetLeftPart(UriPartial.Authority)}'.");
        }
    }

    private static bool HasConfiguredProvenance(CoordinatorUpdateManifestOptions manifest) =>
        !string.IsNullOrWhiteSpace(manifest.SourceRepository) ||
        !string.IsNullOrWhiteSpace(manifest.SourceRevision) ||
        !string.IsNullOrWhiteSpace(manifest.BuildIdentifier) ||
        !string.IsNullOrWhiteSpace(manifest.PublishedBy) ||
        !string.IsNullOrWhiteSpace(manifest.ProvenanceUri);

    private static bool HasConfiguredSignature(CoordinatorUpdateManifestOptions manifest) =>
        !string.IsNullOrWhiteSpace(manifest.SignerId) ||
        !string.IsNullOrWhiteSpace(manifest.Signature);

    private static IReadOnlyDictionary<string, RunnerTrustedManifestSigner> BuildTrustedSignerLookup(IReadOnlyList<RunnerTrustedManifestSigner> signers)
    {
        var lookup = new Dictionary<string, RunnerTrustedManifestSigner>(StringComparer.OrdinalIgnoreCase);
        foreach (var signer in signers)
        {
            if (string.IsNullOrWhiteSpace(signer.SignerId))
            {
                throw new InvalidOperationException("Coordinator trusted manifest signer entries require a signer id.");
            }

            if (string.IsNullOrWhiteSpace(signer.PublicKeyPem))
            {
                throw new InvalidOperationException($"Coordinator trusted manifest signer '{signer.SignerId}' requires a public key.");
            }

            if (!lookup.TryAdd(signer.SignerId.Trim(), signer))
            {
                throw new InvalidOperationException($"Coordinator trusted manifest signer '{signer.SignerId}' is duplicated.");
            }
        }

        return lookup;
    }
}
