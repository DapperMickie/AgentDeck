using System.Net.Http.Json;
using System.Text.Json;
using AgentDeck.Runner.Configuration;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;
using Microsoft.Extensions.Options;

namespace AgentDeck.Runner.Services;

public sealed class RunnerCapabilityCatalogService : IRunnerCapabilityCatalogService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly WorkerCoordinatorOptions _options;
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly ILogger<RunnerCapabilityCatalogService> _logger;
    private RunnerCapabilityCatalog? _currentCatalog;
    private RunnerCapabilityCatalogStatus _currentStatus;

    public RunnerCapabilityCatalogService(
        IOptions<WorkerCoordinatorOptions> options,
        ILogger<RunnerCapabilityCatalogService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _currentCatalog = LoadPersistedCatalog();
        _currentStatus = NormalizePersistedState(_currentCatalog, LoadPersistedStatus());
    }

    public async Task<RunnerCapabilityCatalog?> GetCurrentCatalogAsync(CancellationToken cancellationToken = default)
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

    public async Task<RunnerCapabilityCatalogStatus?> GetCurrentStatusAsync(CancellationToken cancellationToken = default)
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

    public async Task<RunnerCapabilityCatalogStatus> ReconcileDesiredCapabilityCatalogAsync(
        HttpClient coordinatorClient,
        RunnerDesiredState desiredState,
        CancellationToken cancellationToken = default)
    {
        await _reconcileGate.WaitAsync(cancellationToken);
        try
        {
            var localCatalogVersion = _currentCatalog?.Version;
            if (desiredState.DesiredCapabilityCatalog is null || string.IsNullOrWhiteSpace(desiredState.CapabilityCatalogVersion))
            {
                _currentStatus = await PersistStatusAsync(new RunnerCapabilityCatalogStatus
                {
                    State = RunnerCapabilityCatalogState.Unknown,
                    CatalogId = _currentCatalog?.CatalogId,
                    LocalCatalogVersion = localCatalogVersion,
                    StatusMessage = "Coordinator did not provide a desired capability catalog."
                }, cancellationToken);
                return _currentStatus;
            }

            var desiredCatalogId = desiredState.DesiredCapabilityCatalog.DefinitionId;
            var desiredCatalogVersion = desiredState.DesiredCapabilityCatalog.Version;
            if (_currentCatalog is not null &&
                string.Equals(_currentCatalog.CatalogId, desiredCatalogId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_currentCatalog.Version, desiredCatalogVersion, StringComparison.OrdinalIgnoreCase))
            {
                _currentStatus = await PersistStatusAsync(new RunnerCapabilityCatalogStatus
                {
                    State = RunnerCapabilityCatalogState.Matched,
                    CatalogId = _currentCatalog.CatalogId,
                    LocalCatalogVersion = _currentCatalog.Version,
                    DesiredCatalogVersion = desiredCatalogVersion,
                    StatusMessage = $"Runner capability catalog {_currentCatalog.Version} matches the coordinator."
                }, cancellationToken);
                return _currentStatus;
            }

            try
            {
                var catalog = await coordinatorClient.GetFromJsonAsync<RunnerCapabilityCatalog>(
                    $"api/runner-definitions/capability-catalogs/{Uri.EscapeDataString(desiredCatalogId)}",
                    cancellationToken);

                if (catalog is null)
                {
                    throw new InvalidOperationException("Coordinator returned an empty capability catalog response.");
                }

                if (!string.Equals(catalog.CatalogId, desiredCatalogId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Coordinator capability catalog id '{catalog.CatalogId}' did not match desired catalog '{desiredCatalogId}'.");
                }

                if (!string.Equals(catalog.Version, desiredCatalogVersion, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Coordinator capability catalog version '{catalog.Version}' did not match desired version '{desiredCatalogVersion}'.");
                }

                Directory.CreateDirectory(GetCapabilityCatalogRoot());
                await WriteTextAtomicallyAsync(GetCatalogPath(), JsonSerializer.Serialize(catalog, JsonOptions), cancellationToken);
                _currentCatalog = catalog;
                _currentStatus = await PersistStatusAsync(new RunnerCapabilityCatalogStatus
                {
                    State = RunnerCapabilityCatalogState.Matched,
                    CatalogId = catalog.CatalogId,
                    LocalCatalogVersion = catalog.Version,
                    DesiredCatalogVersion = desiredCatalogVersion,
                    StatusMessage = $"Runner capability catalog {catalog.Version} matches the coordinator."
                }, cancellationToken);
                return _currentStatus;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to reconcile desired capability catalog {CatalogId}@{CatalogVersion}", desiredCatalogId, desiredCatalogVersion);
                _currentStatus = await PersistStatusAsync(new RunnerCapabilityCatalogStatus
                {
                    State = RunnerCapabilityCatalogState.Failed,
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

    private async Task<RunnerCapabilityCatalogStatus> PersistStatusAsync(RunnerCapabilityCatalogStatus status, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(GetCapabilityCatalogRoot());
        await WriteTextAtomicallyAsync(GetStatusPath(), JsonSerializer.Serialize(status, JsonOptions), cancellationToken);
        return status;
    }

    private RunnerCapabilityCatalog? LoadPersistedCatalog()
    {
        var path = GetCatalogPath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RunnerCapabilityCatalog>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted capability catalog from {CatalogPath}", path);
            return null;
        }
    }

    private RunnerCapabilityCatalogStatus? LoadPersistedStatus()
    {
        var path = GetStatusPath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RunnerCapabilityCatalogStatus>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted capability catalog status from {StatusPath}", path);
            return null;
        }
    }

    private RunnerCapabilityCatalogStatus NormalizePersistedState(
        RunnerCapabilityCatalog? catalog,
        RunnerCapabilityCatalogStatus? status)
    {
        if (catalog is null)
        {
            if (status is not null)
            {
                _logger.LogWarning("Discarding persisted capability catalog status because no persisted catalog was available.");
            }

            return new RunnerCapabilityCatalogStatus
            {
                State = RunnerCapabilityCatalogState.Unknown,
                StatusMessage = "Runner has not reconciled a coordinator capability catalog yet."
            };
        }

        if (status is null)
        {
            return new RunnerCapabilityCatalogStatus
            {
                State = RunnerCapabilityCatalogState.Unknown,
                CatalogId = catalog.CatalogId,
                LocalCatalogVersion = catalog.Version,
                StatusMessage = $"Loaded persisted capability catalog {catalog.CatalogId}@{catalog.Version}."
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
            "Discarding inconsistent persisted capability catalog status for {CatalogId}@{CatalogVersion}; status referenced {StatusCatalogId}@{StatusCatalogVersion}.",
            catalog.CatalogId,
            catalog.Version,
            status.CatalogId,
            status.LocalCatalogVersion);

        return new RunnerCapabilityCatalogStatus
        {
            State = RunnerCapabilityCatalogState.Unknown,
            CatalogId = catalog.CatalogId,
            LocalCatalogVersion = catalog.Version,
            DesiredCatalogVersion = status.DesiredCatalogVersion,
            StatusMessage = $"Loaded persisted capability catalog {catalog.CatalogId}@{catalog.Version}; coordinator reconciliation will refresh status."
        };
    }

    private string GetCapabilityCatalogRoot()
    {
        if (!string.IsNullOrWhiteSpace(_options.CapabilityCatalogRoot))
        {
            return Path.GetFullPath(_options.CapabilityCatalogRoot);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentDeck",
            "worker",
            "capability-catalog");
    }

    private string GetCatalogPath() => Path.Combine(GetCapabilityCatalogRoot(), "current-capability-catalog.json");

    private string GetStatusPath() => Path.Combine(GetCapabilityCatalogRoot(), "current-capability-catalog-status.json");

    private static async Task WriteTextAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException($"Path '{path}' has no parent directory.");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(tempPath, content, cancellationToken);
        File.Move(tempPath, path, overwrite: true);
    }
}
