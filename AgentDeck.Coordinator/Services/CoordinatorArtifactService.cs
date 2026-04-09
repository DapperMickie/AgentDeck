using System.Security.Cryptography;
using AgentDeck.Coordinator.Configuration;
using Microsoft.Extensions.Options;

namespace AgentDeck.Coordinator.Services;

public sealed class CoordinatorArtifactService : ICoordinatorArtifactService
{
    private readonly string _artifactRoot;

    public CoordinatorArtifactService(IOptions<CoordinatorOptions> coordinatorOptions, IHostEnvironment hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(coordinatorOptions);
        ArgumentNullException.ThrowIfNull(hostEnvironment);

        var configuredRoot = coordinatorOptions.Value.ArtifactRoot;
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            throw new InvalidOperationException(
                $"Coordinator setting '{CoordinatorOptions.SectionName}:ArtifactRoot' is required.");
        }

        _artifactRoot = Path.GetFullPath(
            Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.Combine(hostEnvironment.ContentRootPath, configuredRoot.Trim()));
    }

    public CoordinatorHostedArtifact ResolveHostedArtifact(string relativePath, string? publicBaseUrl)
    {
        var normalizedRelativePath = NormalizeRelativePath(relativePath);
        var physicalPath = GetValidatedArtifactPath(normalizedRelativePath);
        var fileInfo = new FileInfo(physicalPath);
        if (!fileInfo.Exists)
        {
            throw new InvalidOperationException(
                $"Coordinator hosted artifact '{normalizedRelativePath}' was not found under '{_artifactRoot}'.");
        }

        if (!Uri.TryCreate(NormalizeRequired(publicBaseUrl, $"{CoordinatorOptions.SectionName}:PublicBaseUrl"), UriKind.Absolute, out var publicBaseUri))
        {
            throw new InvalidOperationException(
                $"Coordinator setting '{CoordinatorOptions.SectionName}:PublicBaseUrl' must be an absolute URI when hosted artifacts are enabled.");
        }

        return new CoordinatorHostedArtifact(
            normalizedRelativePath,
            physicalPath,
            BuildDownloadUrl(publicBaseUri, normalizedRelativePath),
            ComputeSha256(physicalPath),
            fileInfo.Length);
    }

    public string? TryResolveArtifactPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        try
        {
            var normalizedRelativePath = NormalizeRelativePath(relativePath);
            var physicalPath = GetValidatedArtifactPath(normalizedRelativePath);
            return File.Exists(physicalPath) ? physicalPath : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private string GetValidatedArtifactPath(string relativePath)
    {
        Directory.CreateDirectory(_artifactRoot);

        var candidatePath = Path.GetFullPath(
            Path.Combine(_artifactRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = _artifactRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidatePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidatePath, _artifactRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Coordinator hosted artifact paths must stay inside the configured artifact root.");
        }

        return candidatePath;
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        var segments = relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            throw new InvalidOperationException("Coordinator hosted artifact paths must not be empty.");
        }

        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidOperationException("Coordinator hosted artifact paths must not contain '.' or '..' segments.");
        }

        return string.Join('/', segments);
    }

    private static string BuildDownloadUrl(Uri publicBaseUri, string relativePath)
    {
        var baseUri = publicBaseUri.ToString().TrimEnd('/');
        var encodedPath = string.Join(
            "/",
            relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
        return $"{baseUri}/artifacts/{encodedPath}";
    }

    private static string ComputeSha256(string physicalPath)
    {
        using var stream = File.OpenRead(physicalPath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeRequired(string? value, string settingName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Coordinator setting '{settingName}' is required.")
            : value.Trim();
}
