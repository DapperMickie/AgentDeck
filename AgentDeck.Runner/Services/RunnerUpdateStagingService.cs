using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AgentDeck.Runner.Configuration;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;
using Microsoft.Extensions.Options;

namespace AgentDeck.Runner.Services;

public sealed class RunnerUpdateStagingService : IRunnerUpdateStagingService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly WorkerCoordinatorOptions _options;
    private readonly ILogger<RunnerUpdateStagingService> _logger;
    private RunnerUpdateStatus? _currentStatus;

    public RunnerUpdateStagingService(
        IOptions<WorkerCoordinatorOptions> options,
        ILogger<RunnerUpdateStagingService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _currentStatus = LoadPersistedStatus();
    }

    public async Task<RunnerUpdateStatus?> GetCurrentStatusAsync(CancellationToken cancellationToken = default)
    {
        await _reconcileGate.WaitAsync(cancellationToken);
        try
        {
            return GetCurrentStatusSnapshot();
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    public async Task<RunnerUpdateStatus?> ReconcileDesiredUpdateAsync(
        HttpClient coordinatorClient,
        RunnerDesiredState desiredState,
        CancellationToken cancellationToken = default)
    {
        await _reconcileGate.WaitAsync(cancellationToken);
        try
        {
            if (desiredState.DesiredUpdateManifest is null || !desiredState.UpdateAvailable)
            {
                return await UpdateStatusAsync(null, cancellationToken);
            }

            var desiredManifestId = desiredState.DesiredUpdateManifest.DefinitionId;
            var desiredManifestVersion = desiredState.DesiredUpdateManifest.Version;
            var currentStatus = GetCurrentStatusSnapshot();
            if (currentStatus?.ManifestId == desiredManifestId &&
                currentStatus.ManifestVersion == desiredManifestVersion &&
                (currentStatus.State == RunnerUpdateStageState.PayloadStaged ||
                 (currentStatus.State == RunnerUpdateStageState.ManifestStaged && !_options.DownloadUpdatePayload)))
            {
                return currentStatus;
            }

            try
            {
                var manifest = await coordinatorClient.GetFromJsonAsync<RunnerUpdateManifest>(
                    $"api/runner-definitions/update-manifests/{Uri.EscapeDataString(desiredManifestId)}",
                    cancellationToken);

                if (manifest is null)
                {
                    return await UpdateStatusAsync(new RunnerUpdateStatus
                    {
                        State = RunnerUpdateStageState.Failed,
                        ManifestId = desiredManifestId,
                        ManifestVersion = desiredManifestVersion,
                        FailureMessage = "Coordinator returned an empty update manifest response."
                    }, cancellationToken);
                }

                if (!string.Equals(manifest.ManifestId, desiredManifestId, StringComparison.OrdinalIgnoreCase))
                {
                    return await UpdateStatusAsync(new RunnerUpdateStatus
                    {
                        State = RunnerUpdateStageState.Failed,
                        ManifestId = manifest.ManifestId,
                        ManifestVersion = manifest.Version,
                        FailureMessage = $"Coordinator manifest id '{manifest.ManifestId}' did not match desired manifest '{desiredManifestId}'."
                    }, cancellationToken);
                }

                if (!string.Equals(manifest.Version, desiredManifestVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return await UpdateStatusAsync(new RunnerUpdateStatus
                    {
                        State = RunnerUpdateStageState.Failed,
                        ManifestId = manifest.ManifestId,
                        ManifestVersion = manifest.Version,
                        FailureMessage = $"Coordinator manifest version '{manifest.Version}' did not match desired version '{desiredManifestVersion}'."
                    }, cancellationToken);
                }

                var stagingDirectory = GetStagingDirectory(manifest);
                Directory.CreateDirectory(stagingDirectory);

                var manifestPath = Path.Combine(stagingDirectory, "update-manifest.json");
                await WriteTextAtomicallyAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken);

                var status = new RunnerUpdateStatus
                {
                    State = RunnerUpdateStageState.ManifestStaged,
                    ManifestId = manifest.ManifestId,
                    ManifestVersion = manifest.Version,
                    StagingDirectory = stagingDirectory,
                    StagedAt = DateTimeOffset.UtcNow
                };

                if (_options.DownloadUpdatePayload)
                {
                    if (string.IsNullOrWhiteSpace(manifest.Sha256))
                    {
                        throw new InvalidOperationException("Coordinator update manifest did not include a SHA-256 checksum.");
                    }

                    var artifactUri = GetValidatedArtifactUri(coordinatorClient, manifest.ArtifactUrl);
                    var artifactPath = Path.Combine(stagingDirectory, GetArtifactFileName(artifactUri));
                    var tempArtifactPath = Path.Combine(stagingDirectory, $"{Path.GetFileName(artifactPath)}.{Guid.NewGuid():N}.tmp");
                    try
                    {
                        using var response = await coordinatorClient.GetAsync(artifactUri, cancellationToken);
                        response.EnsureSuccessStatusCode();

                        await using (var destination = File.Create(tempArtifactPath))
                        {
                            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                            await source.CopyToAsync(destination, cancellationToken);
                        }

                        var actualSha256 = await ComputeSha256Async(tempArtifactPath, cancellationToken);
                        if (!string.Equals(actualSha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException(
                                $"Downloaded update artifact SHA-256 '{actualSha256}' did not match manifest SHA-256 '{manifest.Sha256}'.");
                        }

                        File.Move(tempArtifactPath, artifactPath, overwrite: true);
                    }
                    catch
                    {
                        TryDeleteFile(tempArtifactPath);
                        throw;
                    }

                    status = new RunnerUpdateStatus
                    {
                        State = RunnerUpdateStageState.PayloadStaged,
                        ManifestId = manifest.ManifestId,
                        ManifestVersion = manifest.Version,
                        StagingDirectory = stagingDirectory,
                        StagedArtifactPath = artifactPath,
                        StagedAt = DateTimeOffset.UtcNow
                    };
                }

                _logger.LogInformation(
                    "Staged update manifest {ManifestId}@{ManifestVersion} for later application in {StagingDirectory}",
                    status.ManifestId,
                    status.ManifestVersion,
                    status.StagingDirectory);

                return await UpdateStatusAsync(status, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stage desired update manifest {ManifestId}", desiredManifestId);
                return await UpdateStatusAsync(new RunnerUpdateStatus
                {
                    State = RunnerUpdateStageState.Failed,
                    ManifestId = desiredManifestId,
                    ManifestVersion = desiredManifestVersion,
                    FailureMessage = ex.Message
                }, cancellationToken);
            }
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private async Task<RunnerUpdateStatus?> UpdateStatusAsync(RunnerUpdateStatus? status, CancellationToken cancellationToken)
    {
        var statusPath = GetStatusPath();
        Directory.CreateDirectory(Path.GetDirectoryName(statusPath)!);
        RunnerUpdateStatus? previousStatus;
        lock (_lock)
        {
            previousStatus = _currentStatus;
            _currentStatus = status;
        }

        try
        {
            if (status is null)
            {
                TryDeleteFile(statusPath);
                return null;
            }

            await WriteTextAtomicallyAsync(statusPath, JsonSerializer.Serialize(status, JsonOptions), cancellationToken);
            return status;
        }
        catch
        {
            lock (_lock)
            {
                _currentStatus = previousStatus;
            }

            throw;
        }
    }

    private RunnerUpdateStatus? LoadPersistedStatus()
    {
        var statusPath = GetStatusPath();
        if (!File.Exists(statusPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(statusPath);
            return JsonSerializer.Deserialize<RunnerUpdateStatus>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted runner update staging status from {StatusPath}", statusPath);
            return null;
        }
    }

    private string GetStatusPath() => Path.Combine(GetStagingRoot(), "current-update-status.json");

    private RunnerUpdateStatus? GetCurrentStatusSnapshot()
    {
        lock (_lock)
        {
            return _currentStatus;
        }
    }

    private string GetStagingDirectory(RunnerUpdateManifest manifest) =>
        Path.Combine(GetStagingRoot(), SanitizePathComponent(manifest.ManifestId), SanitizePathComponent(manifest.Version));

    private string GetStagingRoot()
    {
        if (!string.IsNullOrWhiteSpace(_options.UpdateStagingRoot))
        {
            return _options.UpdateStagingRoot.Trim();
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentDeck",
            "runner-updates");
    }

    private static string GetArtifactFileName(Uri artifactUri)
    {
        var fileName = artifactUri.Segments.LastOrDefault();
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            return SanitizePathComponent(fileName);
        }

        return "runner-update-artifact.bin";
    }

    private static Uri GetValidatedArtifactUri(HttpClient coordinatorClient, string artifactUrl)
    {
        if (!Uri.TryCreate(artifactUrl, UriKind.RelativeOrAbsolute, out var artifactUri))
        {
            throw new InvalidOperationException($"Coordinator update artifact URL '{artifactUrl}' is invalid.");
        }

        if (!artifactUri.IsAbsoluteUri)
        {
            return artifactUri;
        }

        if (!string.Equals(artifactUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(artifactUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Coordinator update artifact URL '{artifactUrl}' must use HTTP or HTTPS.");
        }

        var coordinatorBaseAddress = coordinatorClient.BaseAddress
            ?? throw new InvalidOperationException("Coordinator client base address is required for update downloads.");

        var sameOrigin = string.Equals(coordinatorBaseAddress.Scheme, artifactUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(coordinatorBaseAddress.Host, artifactUri.Host, StringComparison.OrdinalIgnoreCase) &&
            coordinatorBaseAddress.Port == artifactUri.Port;
        if (!sameOrigin)
        {
            throw new InvalidOperationException(
                $"Coordinator update artifact URL '{artifactUrl}' must match coordinator origin '{coordinatorBaseAddress.GetLeftPart(UriPartial.Authority)}'.");
        }

        return artifactUri;
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static async Task WriteTextAtomicallyAsync(string path, string contents, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException($"Path '{path}' has no parent directory.");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(tempPath, contents, cancellationToken);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete invalid staged update artifact {ArtifactPath}", path);
        }
    }

    public void Dispose()
    {
        _reconcileGate.Dispose();
    }

    private static string SanitizePathComponent(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Update staging path component cannot be empty.");
        }

        var trimmed = value.Trim();
        if (trimmed.Contains("..", StringComparison.Ordinal) ||
            trimmed.Contains(Path.DirectorySeparatorChar) ||
            trimmed.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidOperationException($"Update staging path component '{value}' is invalid.");
        }

        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(trimmed.Select(ch => invalidCharacters.Contains(ch) ? '-' : ch).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            throw new InvalidOperationException($"Update staging path component '{value}' is invalid.");
        }

        return sanitized;
    }
}
