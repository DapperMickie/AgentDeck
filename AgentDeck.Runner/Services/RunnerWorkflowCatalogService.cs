using AgentDeck.Runner.Configuration;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;
using Microsoft.Extensions.Options;

namespace AgentDeck.Runner.Services;

public sealed class RunnerWorkflowCatalogService : IRunnerWorkflowCatalogService, IDisposable
{
    private readonly WorkerCoordinatorOptions _options;
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private RunnerWorkflowCatalogStatus _currentStatus;

    public RunnerWorkflowCatalogService(IOptions<WorkerCoordinatorOptions> options)
    {
        _options = options.Value;
        _currentStatus = new RunnerWorkflowCatalogStatus
        {
            State = RunnerWorkflowCatalogState.Unknown,
            LocalCatalogVersion = GetLocalCatalogVersion(),
            StatusMessage = "Runner has not received a coordinator workflow catalog version yet."
        };
    }

    public async Task<RunnerWorkflowCatalogStatus?> GetCurrentStatusAsync(CancellationToken cancellationToken = default)
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

    public async Task<RunnerWorkflowCatalogStatus> ReconcileDesiredWorkflowCatalogAsync(
        RunnerDesiredState desiredState,
        CancellationToken cancellationToken = default)
    {
        await _reconcileGate.WaitAsync(cancellationToken);
        try
        {
            var localCatalogVersion = GetLocalCatalogVersion();
            RunnerWorkflowCatalogStatus status;
            if (string.IsNullOrWhiteSpace(desiredState.WorkflowCatalogVersion))
            {
                status = new RunnerWorkflowCatalogStatus
                {
                    State = RunnerWorkflowCatalogState.Unknown,
                    LocalCatalogVersion = localCatalogVersion,
                    StatusMessage = "Coordinator did not provide a desired workflow catalog version."
                };
            }
            else if (string.Equals(localCatalogVersion, desiredState.WorkflowCatalogVersion, StringComparison.OrdinalIgnoreCase))
            {
                status = new RunnerWorkflowCatalogStatus
                {
                    State = RunnerWorkflowCatalogState.Matched,
                    LocalCatalogVersion = localCatalogVersion,
                    DesiredCatalogVersion = desiredState.WorkflowCatalogVersion,
                    StatusMessage = $"Runner workflow catalog version {localCatalogVersion} matches the coordinator."
                };
            }
            else
            {
                status = new RunnerWorkflowCatalogStatus
                {
                    State = RunnerWorkflowCatalogState.Mismatched,
                    LocalCatalogVersion = localCatalogVersion,
                    DesiredCatalogVersion = desiredState.WorkflowCatalogVersion,
                    StatusMessage = $"Runner workflow catalog version {localCatalogVersion} does not match coordinator version {desiredState.WorkflowCatalogVersion}."
                };
            }

            _currentStatus = status;
            return status;
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    public void Dispose() => _reconcileGate.Dispose();

    private string GetLocalCatalogVersion() => _options.WorkflowCatalogVersion.Trim();
}
