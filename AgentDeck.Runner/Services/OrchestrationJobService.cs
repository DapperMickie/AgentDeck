using System.Collections.Concurrent;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

/// <inheritdoc />
public sealed class OrchestrationJobService : IOrchestrationJobService
{
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, OrchestrationJob> _jobs = new();

    public OrchestrationJob Queue(CreateOrchestrationJobRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var job = new OrchestrationJob
        {
            Id = Guid.NewGuid().ToString("N"),
            ProjectId = request.ProjectId,
            ProjectName = request.ProjectName,
            LaunchProfileId = request.LaunchProfileId,
            LaunchProfileName = request.LaunchProfileName,
            Platform = request.Platform,
            Mode = request.Mode,
            LaunchDriver = request.LaunchDriver,
            TargetMachineRole = request.TargetMachineRole,
            TargetMachineId = request.TargetMachineId,
            TargetMachineName = request.TargetMachineName,
            WorkingDirectory = request.WorkingDirectory,
            BuildCommand = request.BuildCommand,
            LaunchCommand = request.LaunchCommand,
            BootstrapCommand = request.BootstrapCommand,
            DebugConfigurationName = request.DebugConfigurationName,
            Status = OrchestrationJobStatus.Queued,
            StatusMessage = "Queued for orchestration.",
            CreatedAt = now,
            UpdatedAt = now,
            Steps = CreateSteps(request),
            Logs =
            [
                new OrchestrationJobLogEntry
                {
                    Timestamp = now,
                    Level = OrchestrationLogLevel.Information,
                    Message = "Job queued.",
                    MachineId = request.TargetMachineId
                }
            ]
        };

        lock (_gate)
        {
            _jobs[job.Id] = job;
        }

        return CloneJob(job);
    }

    public OrchestrationJob? Get(string jobId)
    {
        lock (_gate)
        {
            return _jobs.TryGetValue(jobId, out var job) ? CloneJob(job) : null;
        }
    }

    public IReadOnlyList<OrchestrationJob> GetAll() =>
        GetAllInternal();

    public OrchestrationJob? UpdateStatus(string jobId, UpdateOrchestrationJobStatusRequest request)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
            {
                return null;
            }

            if (!IsValidTransition(job.Status, request.Status))
            {
                return null;
            }

            var updatedJob = CloneJob(job);
            updatedJob.Status = request.Status;
            updatedJob.StatusMessage = request.Message ?? updatedJob.StatusMessage;
            updatedJob.SessionId = request.SessionId ?? updatedJob.SessionId;
            updatedJob.ExitCode = request.ExitCode ?? updatedJob.ExitCode;
            updatedJob.UpdatedAt = DateTimeOffset.UtcNow;
            updatedJob.Steps = UpdateSteps(updatedJob.Steps, updatedJob.Status, updatedJob.StatusMessage);

            _jobs[jobId] = updatedJob;
            return CloneJob(updatedJob);
        }
    }

    public OrchestrationJob? AppendLog(string jobId, AppendOrchestrationJobLogRequest request)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
            {
                return null;
            }

            var updatedJob = CloneJob(job);
            updatedJob.UpdatedAt = DateTimeOffset.UtcNow;
            updatedJob.Logs =
            [
                ..updatedJob.Logs,
                new OrchestrationJobLogEntry
                {
                    Timestamp = updatedJob.UpdatedAt,
                    Level = request.Level,
                    Message = request.Message,
                    MachineId = request.MachineId
                }
            ];

            _jobs[jobId] = updatedJob;
            return CloneJob(updatedJob);
        }
    }

    public OrchestrationJob? RequestCancellation(string jobId, string? message = null)
    {
        return UpdateStatus(jobId, new UpdateOrchestrationJobStatusRequest
        {
            Status = OrchestrationJobStatus.CancelRequested,
            Message = message ?? "Cancellation requested."
        });
    }

    private static IReadOnlyList<OrchestrationJobStep> CreateSteps(CreateOrchestrationJobRequest request)
    {
        var steps = new List<OrchestrationJobStep>
        {
            new()
            {
                Name = "Build",
                Status = OrchestrationJobStepStatus.Pending,
                Message = request.BuildCommand
            },
            new()
            {
                Name = "Dispatch",
                Status = OrchestrationJobStepStatus.Pending,
                Message = request.TargetMachineName ?? request.TargetMachineId ?? request.TargetMachineRole.ToString()
            }
        };

        if (request.LaunchDriver == ProjectLaunchDriver.VsCode)
        {
            steps.Add(new OrchestrationJobStep
            {
                Name = "Bootstrap IDE",
                Status = OrchestrationJobStepStatus.Pending,
                Message = request.BootstrapCommand
            });
        }

        steps.Add(new OrchestrationJobStep
        {
            Name = request.Mode == ProjectLaunchMode.Debug ? "Debug" : "Launch",
            Status = OrchestrationJobStepStatus.Pending,
            Message = request.Mode == ProjectLaunchMode.Debug
                ? request.DebugConfigurationName ?? request.BootstrapCommand
                : request.LaunchCommand
        });

        return steps;
    }

    private static IReadOnlyList<OrchestrationJobStep> UpdateSteps(
        IReadOnlyList<OrchestrationJobStep> steps,
        OrchestrationJobStatus status,
        string? message)
    {
        var now = DateTimeOffset.UtcNow;
        var updatedSteps = steps.ToList();

        if (updatedSteps.Count == 0)
        {
            return updatedSteps;
        }

        if (status == OrchestrationJobStatus.Queued)
        {
            return updatedSteps;
        }

        if (status is OrchestrationJobStatus.Preparing or OrchestrationJobStatus.Dispatching or OrchestrationJobStatus.Running)
        {
            var nextPendingIndex = updatedSteps.FindIndex(step => step.Status == OrchestrationJobStepStatus.Pending);
            if (nextPendingIndex >= 0)
            {
                var nextPending = updatedSteps[nextPendingIndex];
                updatedSteps[nextPendingIndex] = new OrchestrationJobStep
                {
                    Name = nextPending.Name,
                    Status = OrchestrationJobStepStatus.Running,
                    Message = message ?? nextPending.Message,
                    StartedAt = nextPending.StartedAt ?? now,
                    CompletedAt = nextPending.CompletedAt
                };
            }
        }
        else if (status == OrchestrationJobStatus.Completed)
        {
            for (var index = 0; index < updatedSteps.Count; index++)
            {
                var step = updatedSteps[index];
                var nextStatus = step.Status switch
                {
                    OrchestrationJobStepStatus.Running => OrchestrationJobStepStatus.Completed,
                    OrchestrationJobStepStatus.Pending => OrchestrationJobStepStatus.Skipped,
                    _ => step.Status
                };

                updatedSteps[index] = new OrchestrationJobStep
                {
                    Name = step.Name,
                    Status = nextStatus,
                    Message = message ?? step.Message,
                    StartedAt = step.Status == OrchestrationJobStepStatus.Pending ? step.StartedAt : step.StartedAt ?? now,
                    CompletedAt = nextStatus is OrchestrationJobStepStatus.Completed or OrchestrationJobStepStatus.Skipped
                        ? now
                        : step.CompletedAt
                };
            }
        }
        else if (status is OrchestrationJobStatus.Failed or OrchestrationJobStatus.CancelRequested or OrchestrationJobStatus.Cancelled)
        {
            for (var index = 0; index < updatedSteps.Count; index++)
            {
                var active = updatedSteps[index];
                if (active.Status is not (OrchestrationJobStepStatus.Running or OrchestrationJobStepStatus.Pending))
                {
                    continue;
                }

                var nextStatus = status == OrchestrationJobStatus.Failed
                    ? OrchestrationJobStepStatus.Failed
                    : OrchestrationJobStepStatus.Skipped;

                updatedSteps[index] = new OrchestrationJobStep
                {
                    Name = active.Name,
                    Status = nextStatus,
                    Message = message ?? active.Message,
                    StartedAt = active.StartedAt ?? now,
                    CompletedAt = now
                };
            }
        }

        return updatedSteps;
    }

    private IReadOnlyList<OrchestrationJob> GetAllInternal()
    {
        lock (_gate)
        {
            return [.. _jobs.Values
                .OrderByDescending(job => job.CreatedAt)
                .Select(CloneJob)];
        }
    }

    private static bool IsValidTransition(OrchestrationJobStatus from, OrchestrationJobStatus to)
    {
        if (from == to)
        {
            return true;
        }

        return (from, to) switch
        {
            (OrchestrationJobStatus.Queued, OrchestrationJobStatus.Preparing) => true,
            (OrchestrationJobStatus.Queued, OrchestrationJobStatus.CancelRequested) => true,
            (OrchestrationJobStatus.Queued, OrchestrationJobStatus.Cancelled) => true,
            (OrchestrationJobStatus.Preparing, OrchestrationJobStatus.Dispatching) => true,
            (OrchestrationJobStatus.Preparing, OrchestrationJobStatus.Failed) => true,
            (OrchestrationJobStatus.Preparing, OrchestrationJobStatus.CancelRequested) => true,
            (OrchestrationJobStatus.Preparing, OrchestrationJobStatus.Cancelled) => true,
            (OrchestrationJobStatus.Dispatching, OrchestrationJobStatus.Running) => true,
            (OrchestrationJobStatus.Dispatching, OrchestrationJobStatus.Failed) => true,
            (OrchestrationJobStatus.Dispatching, OrchestrationJobStatus.CancelRequested) => true,
            (OrchestrationJobStatus.Dispatching, OrchestrationJobStatus.Cancelled) => true,
            (OrchestrationJobStatus.Running, OrchestrationJobStatus.Completed) => true,
            (OrchestrationJobStatus.Running, OrchestrationJobStatus.Failed) => true,
            (OrchestrationJobStatus.Running, OrchestrationJobStatus.CancelRequested) => true,
            (OrchestrationJobStatus.Running, OrchestrationJobStatus.Cancelled) => true,
            (OrchestrationJobStatus.CancelRequested, OrchestrationJobStatus.Cancelled) => true,
            (OrchestrationJobStatus.CancelRequested, OrchestrationJobStatus.Failed) => true,
            _ => false
        };
    }

    private static OrchestrationJob CloneJob(OrchestrationJob job)
    {
        return new OrchestrationJob
        {
            Id = job.Id,
            ProjectId = job.ProjectId,
            ProjectName = job.ProjectName,
            LaunchProfileId = job.LaunchProfileId,
            LaunchProfileName = job.LaunchProfileName,
            Platform = job.Platform,
            Mode = job.Mode,
            LaunchDriver = job.LaunchDriver,
            TargetMachineRole = job.TargetMachineRole,
            TargetMachineId = job.TargetMachineId,
            TargetMachineName = job.TargetMachineName,
            WorkingDirectory = job.WorkingDirectory,
            BuildCommand = job.BuildCommand,
            LaunchCommand = job.LaunchCommand,
            BootstrapCommand = job.BootstrapCommand,
            DebugConfigurationName = job.DebugConfigurationName,
            Status = job.Status,
            SessionId = job.SessionId,
            ExitCode = job.ExitCode,
            StatusMessage = job.StatusMessage,
            CreatedAt = job.CreatedAt,
            UpdatedAt = job.UpdatedAt,
            Steps =
            [
                .. job.Steps.Select(step => new OrchestrationJobStep
                {
                    Name = step.Name,
                    Status = step.Status,
                    Message = step.Message,
                    StartedAt = step.StartedAt,
                    CompletedAt = step.CompletedAt
                })
            ],
            Logs =
            [
                .. job.Logs.Select(log => new OrchestrationJobLogEntry
                {
                    Timestamp = log.Timestamp,
                    Level = log.Level,
                    Message = log.Message,
                    MachineId = log.MachineId
                })
            ]
        };
    }
}
