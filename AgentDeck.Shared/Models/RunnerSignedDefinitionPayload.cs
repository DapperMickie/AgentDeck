using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentDeck.Shared.Models;

/// <summary>Canonical signing helpers for coordinator-published runner definitions that can drive local execution.</summary>
public static class RunnerSignedDefinitionPayload
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool VerifySignature(RunnerWorkflowPack pack, string publicKeyPem, out string error) =>
        VerifySignature("workflow pack", pack.Signature, BuildSigningPayload(pack), publicKeyPem, out error);

    public static bool VerifySignature(RunnerCapabilityCatalog catalog, string publicKeyPem, out string error) =>
        VerifySignature("capability catalog", catalog.Signature, BuildSigningPayload(catalog), publicKeyPem, out error);

    public static bool VerifySignature(RunnerSetupCatalog catalog, string publicKeyPem, out string error) =>
        VerifySignature("setup catalog", catalog.Signature, BuildSigningPayload(catalog), publicKeyPem, out error);

    public static bool TrySignWorkflowPack(RunnerWorkflowPack pack, string privateKeyPem, out string signatureValue, out string error) =>
        TrySign(BuildSigningPayload(pack), privateKeyPem, out signatureValue, out error);

    public static bool TrySignCapabilityCatalog(RunnerCapabilityCatalog catalog, string privateKeyPem, out string signatureValue, out string error) =>
        TrySign(BuildSigningPayload(catalog), privateKeyPem, out signatureValue, out error);

    public static bool TrySignSetupCatalog(RunnerSetupCatalog catalog, string privateKeyPem, out string signatureValue, out string error) =>
        TrySign(BuildSigningPayload(catalog), privateKeyPem, out signatureValue, out error);

    public static string BuildSigningPayload(RunnerWorkflowPack pack) => JsonSerializer.Serialize(new
    {
        pack.PackId,
        pack.Version,
        pack.DisplayName,
        pack.Description,
        pack.Steps,
        pack.Provenance
    }, JsonOptions);

    public static string BuildSigningPayload(RunnerCapabilityCatalog catalog) => JsonSerializer.Serialize(new
    {
        catalog.CatalogId,
        catalog.Version,
        catalog.DisplayName,
        catalog.Description,
        catalog.Capabilities,
        catalog.Provenance
    }, JsonOptions);

    public static string BuildSigningPayload(RunnerSetupCatalog catalog) => JsonSerializer.Serialize(new
    {
        catalog.CatalogId,
        catalog.Version,
        catalog.DisplayName,
        catalog.Description,
        catalog.Capabilities,
        catalog.Provenance
    }, JsonOptions);

    private static bool VerifySignature(string payloadName, RunnerUpdateManifestSignature? signature, string payload, string publicKeyPem, out string error)
    {
        if (signature is null)
        {
            error = $"Signed {payloadName} signature is required.";
            return false;
        }

        if (!string.Equals(signature.Algorithm, RunnerUpdateManifestSigning.RsaSha256Algorithm, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Unsupported {payloadName} signature algorithm '{signature.Algorithm}'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            error = $"Trusted {payloadName} signer public key is required.";
            return false;
        }

        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(signature.Value);
        }
        catch (FormatException)
        {
            error = $"{payloadName} signature for signer '{signature.SignerId}' is not valid Base64.";
            return false;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            var verified = rsa.VerifyData(Encoding.UTF8.GetBytes(payload), signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            error = verified ? string.Empty : $"{payloadName} signature verification failed for signer '{signature.SignerId}'.";
            return verified;
        }
        catch (CryptographicException)
        {
            error = $"Trusted {payloadName} signer public key for '{signature.SignerId}' is invalid.";
            return false;
        }
    }

    private static bool TrySign(string payload, string privateKeyPem, out string signatureValue, out string error)
    {
        if (string.IsNullOrWhiteSpace(privateKeyPem))
        {
            signatureValue = string.Empty;
            error = "Definition signing private key is required.";
            return false;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKeyPem);
            var signatureBytes = rsa.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            signatureValue = Convert.ToBase64String(signatureBytes);
            error = string.Empty;
            return true;
        }
        catch (CryptographicException)
        {
            signatureValue = string.Empty;
            error = "Definition signing private key is invalid.";
            return false;
        }
    }
}
