using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentDeck.Shared.Models;

/// <summary>Canonicalization and verification helpers for runner update manifest provenance and signatures.</summary>
public static class RunnerUpdateManifestSigning
{
    public const string RsaSha256Algorithm = "RSA-SHA256";

    public static bool HasRequiredProvenance(RunnerUpdateManifest manifest, out string error)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var provenance = manifest.Provenance;
        if (provenance is null)
        {
            error = "Update manifest provenance is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(provenance.SourceRepository))
        {
            error = "Update manifest provenance must include a source repository.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(provenance.SourceRevision))
        {
            error = "Update manifest provenance must include a source revision.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool VerifySignature(RunnerUpdateManifest manifest, string publicKeyPem, out string error)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.Signature is null)
        {
            error = "Update manifest signature is required.";
            return false;
        }

        if (!string.Equals(manifest.Signature.Algorithm, RsaSha256Algorithm, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Unsupported update manifest signature algorithm '{manifest.Signature.Algorithm}'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            error = "Trusted manifest signer public key is required.";
            return false;
        }

        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(manifest.Signature.Value);
        }
        catch (FormatException)
        {
            error = $"Update manifest signature for signer '{manifest.Signature.SignerId}' is not valid Base64.";
            return false;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            var payloadBytes = Encoding.UTF8.GetBytes(BuildSigningPayload(manifest));
            var verified = rsa.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            error = verified
                ? string.Empty
                : $"Update manifest signature verification failed for signer '{manifest.Signature.SignerId}'.";
            return verified;
        }
        catch (CryptographicException)
        {
            error = $"Trusted manifest signer public key for '{manifest.Signature.SignerId}' is invalid.";
            return false;
        }
    }

    public static string BuildSigningPayload(RunnerUpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var provenance = manifest.Provenance;
        return string.Join(
            "\n",
            [
                $"ManifestId={EncodeForPayload(manifest.ManifestId)}",
                $"Version={EncodeForPayload(manifest.Version)}",
                $"Channel={EncodeForPayload(manifest.Channel)}",
                $"ArtifactUrl={EncodeForPayload(manifest.ArtifactUrl)}",
                $"Sha256={EncodeForPayload(manifest.Sha256)}",
                $"ArtifactSizeBytes={EncodeForPayload(manifest.ArtifactSizeBytes?.ToString(CultureInfo.InvariantCulture))}",
                $"MinimumProtocolVersion={EncodeForPayload(manifest.MinimumProtocolVersion.ToString(CultureInfo.InvariantCulture))}",
                $"MaximumProtocolVersion={EncodeForPayload(manifest.MaximumProtocolVersion.ToString(CultureInfo.InvariantCulture))}",
                $"PublishedAt={EncodeForPayload(manifest.PublishedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))}",
                $"Notes={EncodeForPayload(manifest.Notes)}",
                $"SourceRepository={EncodeForPayload(provenance?.SourceRepository)}",
                $"SourceRevision={EncodeForPayload(provenance?.SourceRevision)}",
                $"BuildIdentifier={EncodeForPayload(provenance?.BuildIdentifier)}",
                $"PublishedBy={EncodeForPayload(provenance?.PublishedBy)}",
                $"ProvenanceUri={EncodeForPayload(provenance?.ProvenanceUri)}"
            ]);
    }

    private static string EncodeForPayload(string? value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
}
