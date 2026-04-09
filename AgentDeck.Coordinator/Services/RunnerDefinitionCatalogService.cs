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
            Notes = NormalizeOptional(desiredUpdateManifest.Notes)
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
        if (securityPolicy.RequireUpdateArtifactChecksum && string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            throw new InvalidOperationException(
                $"Coordinator setting '{CoordinatorOptions.SectionName}:DesiredUpdateManifest:Sha256' is required when update artifact checksums are enforced.");
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
}
