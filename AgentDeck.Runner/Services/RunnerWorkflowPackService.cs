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
                    StatusMessage = $"Coordinator security policy {desiredState.SecurityPolicy.PolicyVersion} does not permit workflow pack execution."
                }, cancellationToken);
            }

            var currentStatus = GetCurrentStatusSnapshot();
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
                        FailureMessage = "Coordinator returned an empty workflow pack response."
                    }, cancellationToken);
                }

                if (!string.Equals(pack.PackId, desiredPackId, StringComparison.OrdinalIgnoreCase))
                {
                    return await UpdateStatusAsync(new RunnerWorkflowPackStatus
                    {
                        State = RunnerWorkflowPackState.Failed,
                        PackId = pack.PackId,
                        PackVersion = pack.Version,
                        FailureMessage = $"Coordinator workflow pack id '{pack.PackId}' did not match desired pack '{desiredPackId}'."
                    }, cancellationToken);
                }

                if (!string.Equals(pack.Version, desiredPackVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return await UpdateStatusAsync(new RunnerWorkflowPackStatus
                    {
                        State = RunnerWorkflowPackState.Failed,
                        PackId = pack.PackId,
                        PackVersion = pack.Version,
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

    private static async Task WriteTextAtomicallyAsync(string path, string contents, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("A target file path is required.");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(tempPath, contents, cancellationToken);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        File.Move(tempPath, path);
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
