using System.Net.Http.Json;
using System.Text.Json;
using AgentDeck.Runner.Configuration;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;
using Microsoft.Extensions.Options;

namespace AgentDeck.Runner.Services;

public sealed class RunnerWorkflowPackService : IRunnerWorkflowPackService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly WorkerCoordinatorOptions _options;
    private readonly ILogger<RunnerWorkflowPackService> _logger;
    private RunnerWorkflowPackStatus? _currentStatus;

    public RunnerWorkflowPackService(
        IOptions<WorkerCoordinatorOptions> options,
        ILogger<RunnerWorkflowPackService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _currentStatus = LoadPersistedStatus();
    }

    public async Task<RunnerWorkflowPackStatus?> GetCurrentStatusAsync(CancellationToken cancellationToken = default)
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

    public async Task<RunnerWorkflowPackStatus?> ReconcileDesiredWorkflowPackAsync(
        HttpClient coordinatorClient,
        RunnerDesiredState desiredState,
        CancellationToken cancellationToken = default)
    {
        await _reconcileGate.WaitAsync(cancellationToken);
        try
        {
            var currentStatus = GetCurrentStatusSnapshot();
            if (desiredState.DesiredWorkflowPack is null)
            {
                return await UpdateStatusAsync(null, cancellationToken);
            }

            var desiredPackId = desiredState.DesiredWorkflowPack.DefinitionId;
            var desiredPackVersion = desiredState.DesiredWorkflowPack.Version;
            if (!desiredState.SecurityPolicy.AllowWorkflowPackExecution)
            {
                return await UpdateStatusAsync(new RunnerWorkflowPackStatus
                {
                    State = RunnerWorkflowPackState.Blocked,
                    PackId = desiredPackId,
                    PackVersion = desiredPackVersion,
                    LocalPackPath = GetRetainedLocalPackPath(currentStatus, desiredPackId, desiredPackVersion),
                    FetchedAt = GetRetainedFetchedAt(currentStatus, desiredPackId, desiredPackVersion),
                    StatusMessage = $"Coordinator security policy {desiredState.SecurityPolicy.PolicyVersion} does not permit workflow pack execution."
                }, cancellationToken);
            }

            if (currentStatus?.State == RunnerWorkflowPackState.Ready &&
                string.Equals(currentStatus.PackId, desiredPackId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(currentStatus.PackVersion, desiredPackVersion, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(currentStatus.LocalPackPath) &&
                File.Exists(currentStatus.LocalPackPath))
            {
                return currentStatus;
            }

            try
            {
                var pack = await coordinatorClient.GetFromJsonAsync<RunnerWorkflowPack>(
                    $"api/runner-definitions/workflow-packs/{Uri.EscapeDataString(desiredPackId)}",
                    cancellationToken);

                if (pack is null)
                {
                    return await UpdateStatusAsync(new RunnerWorkflowPackStatus
                    {
                        State = RunnerWorkflowPackState.Failed,
                        PackId = desiredPackId,
                        PackVersion = desiredPackVersion,
                        LocalPackPath = GetRetainedLocalPackPath(currentStatus, desiredPackId, desiredPackVersion),
                        FetchedAt = GetRetainedFetchedAt(currentStatus, desiredPackId, desiredPackVersion),
                        FailureMessage = "Coordinator returned an empty workflow pack response."
                    }, cancellationToken);
                }

                if (!string.Equals(pack.PackId, desiredPackId, StringComparison.OrdinalIgnoreCase))
                {
                    return await UpdateStatusAsync(new RunnerWorkflowPackStatus
                    {
                        State = RunnerWorkflowPackState.Failed,
                        PackId = desiredPackId,
                        PackVersion = desiredPackVersion,
                        LocalPackPath = GetRetainedLocalPackPath(currentStatus, desiredPackId, desiredPackVersion),
                        FetchedAt = GetRetainedFetchedAt(currentStatus, desiredPackId, desiredPackVersion),
                        FailureMessage = $"Coordinator workflow pack id '{pack.PackId}' did not match desired pack '{desiredPackId}'."
                    }, cancellationToken);
                }

                if (!string.Equals(pack.Version, desiredPackVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return await UpdateStatusAsync(new RunnerWorkflowPackStatus
                    {
                        State = RunnerWorkflowPackState.Failed,
                        PackId = desiredPackId,
                        PackVersion = desiredPackVersion,
                        LocalPackPath = GetRetainedLocalPackPath(currentStatus, desiredPackId, desiredPackVersion),
                        FetchedAt = GetRetainedFetchedAt(currentStatus, desiredPackId, desiredPackVersion),
                        FailureMessage = $"Coordinator workflow pack version '{pack.Version}' did not match desired version '{desiredPackVersion}'."
                    }, cancellationToken);
                }

                var packDirectory = Path.Combine(GetWorkflowPackRoot(), SanitizePathComponent(pack.PackId), SanitizePathComponent(pack.Version));
                Directory.CreateDirectory(packDirectory);
                var packPath = Path.Combine(packDirectory, "workflow-pack.json");
                await WriteTextAtomicallyAsync(packPath, JsonSerializer.Serialize(pack, JsonOptions), cancellationToken);

                return await UpdateStatusAsync(new RunnerWorkflowPackStatus
                {
                    State = RunnerWorkflowPackState.Ready,
                    PackId = pack.PackId,
                    PackVersion = pack.Version,
                    LocalPackPath = packPath,
                    FetchedAt = DateTimeOffset.UtcNow,
                    StatusMessage = $"Workflow pack {pack.PackId}@{pack.Version} is available for future runner-side execution."
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to reconcile desired workflow pack {PackId}@{PackVersion}", desiredPackId, desiredPackVersion);
                    return await UpdateStatusAsync(new RunnerWorkflowPackStatus
                    {
                        State = RunnerWorkflowPackState.Failed,
                        PackId = desiredPackId,
                        PackVersion = desiredPackVersion,
                        LocalPackPath = GetRetainedLocalPackPath(currentStatus, desiredPackId, desiredPackVersion),
                        FetchedAt = GetRetainedFetchedAt(currentStatus, desiredPackId, desiredPackVersion),
                        FailureMessage = ex.Message
                    }, cancellationToken);
            }
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    public void Dispose() => _reconcileGate.Dispose();

    private async Task<RunnerWorkflowPackStatus?> UpdateStatusAsync(RunnerWorkflowPackStatus? status, CancellationToken cancellationToken)
    {
        var statusPath = GetStatusPath();
        Directory.CreateDirectory(Path.GetDirectoryName(statusPath)!);
        RunnerWorkflowPackStatus? previousStatus;
        lock (_lock)
        {
            previousStatus = _currentStatus;
        }

        if (status is null)
        {
            TryDeleteFile(statusPath);
            CleanupOldWorkflowPackFiles(previousStatus, null);
            lock (_lock)
            {
                _currentStatus = null;
            }

            return null;
        }

        await WriteTextAtomicallyAsync(statusPath, JsonSerializer.Serialize(status, JsonOptions), cancellationToken);
        CleanupOldWorkflowPackFiles(previousStatus, status);
        lock (_lock)
        {
            _currentStatus = status;
        }

        return status;
    }

    private RunnerWorkflowPackStatus? LoadPersistedStatus()
    {
        var statusPath = GetStatusPath();
        if (!File.Exists(statusPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(statusPath);
            return JsonSerializer.Deserialize<RunnerWorkflowPackStatus>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted workflow pack status from {StatusPath}", statusPath);
            return null;
        }
    }

    private RunnerWorkflowPackStatus? GetCurrentStatusSnapshot()
    {
        lock (_lock)
        {
            return _currentStatus;
        }
    }

    private string GetStatusPath() => Path.Combine(GetWorkflowPackRoot(), "current-workflow-pack-status.json");

    private string GetWorkflowPackRoot()
    {
        if (!string.IsNullOrWhiteSpace(_options.WorkflowPackRoot))
        {
            return _options.WorkflowPackRoot.Trim();
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentDeck",
            "workflow-packs");
    }

    private void CleanupOldWorkflowPackFiles(RunnerWorkflowPackStatus? previousStatus, RunnerWorkflowPackStatus? currentStatus)
    {
        var previousPath = previousStatus?.LocalPackPath;
        if (string.IsNullOrWhiteSpace(previousPath))
        {
            return;
        }

        if (string.Equals(previousPath, currentStatus?.LocalPackPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var previousDirectory = Path.GetDirectoryName(previousPath);
        if (string.IsNullOrWhiteSpace(previousDirectory) || !Directory.Exists(previousDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(previousDirectory, recursive: true);

            var packDirectory = Path.GetDirectoryName(previousDirectory);
            if (!string.IsNullOrWhiteSpace(packDirectory) &&
                Directory.Exists(packDirectory) &&
                !Directory.EnumerateFileSystemEntries(packDirectory).Any())
            {
                Directory.Delete(packDirectory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up stale workflow pack files under {WorkflowPackDirectory}", previousDirectory);
        }
    }

    private static string? GetRetainedLocalPackPath(RunnerWorkflowPackStatus? currentStatus, string desiredPackId, string desiredPackVersion) =>
        IsSamePack(currentStatus, desiredPackId, desiredPackVersion) &&
        !string.IsNullOrWhiteSpace(currentStatus?.LocalPackPath) &&
        File.Exists(currentStatus.LocalPackPath)
            ? currentStatus.LocalPackPath
            : null;

    private static DateTimeOffset? GetRetainedFetchedAt(RunnerWorkflowPackStatus? currentStatus, string desiredPackId, string desiredPackVersion) =>
        IsSamePack(currentStatus, desiredPackId, desiredPackVersion)
            ? currentStatus?.FetchedAt
            : null;

    private static bool IsSamePack(RunnerWorkflowPackStatus? currentStatus, string desiredPackId, string desiredPackVersion) =>
        currentStatus is not null &&
        string.Equals(currentStatus.PackId, desiredPackId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(currentStatus.PackVersion, desiredPackVersion, StringComparison.OrdinalIgnoreCase);

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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static string SanitizePathComponent(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new char[value.Length];
        var index = 0;
        foreach (var character in value)
        {
            builder[index++] = invalidChars.Contains(character) ? '_' : character;
        }

        return new string(builder, 0, index);
    }
}
