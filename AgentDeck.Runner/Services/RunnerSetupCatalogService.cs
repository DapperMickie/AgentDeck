using System.Net.Http.Json;
using System.Text.Json;
using AgentDeck.Runner.Configuration;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;
using Microsoft.Extensions.Options;

namespace AgentDeck.Runner.Services;

public sealed class RunnerSetupCatalogService : IRunnerSetupCatalogService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly WorkerCoordinatorOptions _options;
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly ILogger<RunnerSetupCatalogService> _logger;
    private RunnerSetupCatalog? _currentCatalog;
    private RunnerSetupCatalogStatus _currentStatus;

    public RunnerSetupCatalogService(
        IOptions<WorkerCoordinatorOptions> options,
        ILogger<RunnerSetupCatalogService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _currentCatalog = LoadPersistedCatalog();
        _currentStatus = NormalizePersistedState(_currentCatalog, LoadPersistedStatus());
    }

    public async Task<RunnerSetupCatalog?> GetCurrentCatalogAsync(CancellationToken cancellationToken = default)
    {
        await _reconcileGate.WaitAsync(cancellationToken);
        try
        {
            return _currentCatalog;
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    public async Task<RunnerSetupCatalogStatus?> GetCurrentStatusAsync(CancellationToken cancellationToken = default)
    {
        await _reconcileGate.WaitAsync(cancellationToken);
        try
        {
            return _currentStatus;
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    public async Task<RunnerSetupCatalogStatus> ReconcileDesiredSetupCatalogAsync(
        HttpClient coordinatorClient,
        RunnerDesiredState desiredState,
        CancellationToken cancellationToken = default)
    {
        await _reconcileGate.WaitAsync(cancellationToken);
        try
        {
            var localCatalogVersion = _currentCatalog?.Version;
            if (desiredState.DesiredSetupCatalog is null)
            {
                _currentStatus = await PersistStatusAsync(new RunnerSetupCatalogStatus
                {
                    State = RunnerSetupCatalogState.Unknown,
                    CatalogId = _currentCatalog?.CatalogId,
                    LocalCatalogVersion = localCatalogVersion,
                    StatusMessage = "Coordinator did not provide a desired setup catalog."
                }, cancellationToken);
                return _currentStatus;
            }

            var desiredCatalogId = desiredState.DesiredSetupCatalog.DefinitionId;
            var desiredCatalogVersion = desiredState.DesiredSetupCatalog.Version;
            if (_currentCatalog is not null &&
                string.Equals(_currentCatalog.CatalogId, desiredCatalogId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_currentCatalog.Version, desiredCatalogVersion, StringComparison.OrdinalIgnoreCase))
            {
                _currentStatus = await PersistStatusAsync(new RunnerSetupCatalogStatus
                {
                    State = RunnerSetupCatalogState.Matched,
                    CatalogId = _currentCatalog.CatalogId,
                    LocalCatalogVersion = _currentCatalog.Version,
                    DesiredCatalogVersion = desiredCatalogVersion,
                    StatusMessage = $"Runner setup catalog {_currentCatalog.Version} matches the coordinator."
                }, cancellationToken);
                return _currentStatus;
            }

            try
            {
                var catalog = await coordinatorClient.GetFromJsonAsync<RunnerSetupCatalog>(
                    $"api/runner-definitions/setup-catalogs/{Uri.EscapeDataString(desiredCatalogId)}",
                    cancellationToken);

                if (catalog is null)
                {
                    throw new InvalidOperationException("Coordinator returned an empty setup catalog response.");
                }

                if (!string.Equals(catalog.CatalogId, desiredCatalogId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Coordinator setup catalog id '{catalog.CatalogId}' did not match desired catalog '{desiredCatalogId}'.");
                }

                if (!string.Equals(catalog.Version, desiredCatalogVersion, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Coordinator setup catalog version '{catalog.Version}' did not match desired version '{desiredCatalogVersion}'.");
                }

                Directory.CreateDirectory(GetSetupCatalogRoot());
                await WriteTextAtomicallyAsync(GetCatalogPath(), JsonSerializer.Serialize(catalog, JsonOptions), cancellationToken);
                _currentCatalog = catalog;
                _currentStatus = await PersistStatusAsync(new RunnerSetupCatalogStatus
                {
                    State = RunnerSetupCatalogState.Matched,
                    CatalogId = catalog.CatalogId,
                    LocalCatalogVersion = catalog.Version,
                    DesiredCatalogVersion = desiredCatalogVersion,
                    StatusMessage = $"Runner setup catalog {catalog.Version} matches the coordinator."
                }, cancellationToken);
                return _currentStatus;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to reconcile desired setup catalog {CatalogId}@{CatalogVersion}", desiredCatalogId, desiredCatalogVersion);
                _currentStatus = await PersistStatusAsync(new RunnerSetupCatalogStatus
                {
                    State = RunnerSetupCatalogState.Failed,
                    CatalogId = desiredCatalogId,
                    LocalCatalogVersion = localCatalogVersion,
                    DesiredCatalogVersion = desiredCatalogVersion,
                    StatusMessage = ex.Message
                }, cancellationToken);
                return _currentStatus;
            }
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    public void Dispose() => _reconcileGate.Dispose();

    private async Task<RunnerSetupCatalogStatus> PersistStatusAsync(RunnerSetupCatalogStatus status, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(GetSetupCatalogRoot());
        await WriteTextAtomicallyAsync(GetStatusPath(), JsonSerializer.Serialize(status, JsonOptions), cancellationToken);
        return status;
    }

    private RunnerSetupCatalog? LoadPersistedCatalog()
    {
        var path = GetCatalogPath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RunnerSetupCatalog>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted setup catalog from {CatalogPath}", path);
            return null;
        }
    }

    private RunnerSetupCatalogStatus? LoadPersistedStatus()
    {
        var path = GetStatusPath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RunnerSetupCatalogStatus>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted setup catalog status from {StatusPath}", path);
            return null;
        }
    }

    private RunnerSetupCatalogStatus NormalizePersistedState(
        RunnerSetupCatalog? catalog,
        RunnerSetupCatalogStatus? status)
    {
        if (catalog is null)
        {
            if (status is not null)
            {
                _logger.LogWarning("Discarding persisted setup catalog status because no persisted catalog was available.");
            }

            return new RunnerSetupCatalogStatus
            {
                State = RunnerSetupCatalogState.Unknown,
                StatusMessage = "Runner has not reconciled a coordinator setup catalog yet."
            };
        }

        if (status is null)
        {
            return new RunnerSetupCatalogStatus
            {
                State = RunnerSetupCatalogState.Unknown,
                CatalogId = catalog.CatalogId,
                LocalCatalogVersion = catalog.Version,
                StatusMessage = $"Loaded persisted setup catalog {catalog.CatalogId}@{catalog.Version}."
            };
        }

        var catalogMatchesStatus =
            string.Equals(status.CatalogId, catalog.CatalogId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(status.LocalCatalogVersion, catalog.Version, StringComparison.OrdinalIgnoreCase);

        if (catalogMatchesStatus)
        {
            return status;
        }

        _logger.LogWarning(
            "Discarding inconsistent persisted setup catalog status for {CatalogId}@{CatalogVersion}; status referenced {StatusCatalogId}@{StatusCatalogVersion}.",
            catalog.CatalogId,
            catalog.Version,
            status.CatalogId,
            status.LocalCatalogVersion);

        return new RunnerSetupCatalogStatus
        {
            State = RunnerSetupCatalogState.Unknown,
            CatalogId = catalog.CatalogId,
            LocalCatalogVersion = catalog.Version,
            DesiredCatalogVersion = status.DesiredCatalogVersion,
            StatusMessage = $"Loaded persisted setup catalog {catalog.CatalogId}@{catalog.Version}; coordinator reconciliation will refresh status."
        };
    }

    private string GetSetupCatalogRoot()
    {
        if (!string.IsNullOrWhiteSpace(_options.SetupCatalogRoot))
        {
            return Path.GetFullPath(_options.SetupCatalogRoot);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentDeck",
            "worker",
            "setup-catalog");
    }

    private string GetCatalogPath() => Path.Combine(GetSetupCatalogRoot(), "current-setup-catalog.json");

    private string GetStatusPath() => Path.Combine(GetSetupCatalogRoot(), "current-setup-catalog-status.json");

    private static async Task WriteTextAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException($"Path '{path}' has no parent directory.");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(tempPath, content, cancellationToken);
        File.Move(tempPath, path, overwrite: true);
    }
}
