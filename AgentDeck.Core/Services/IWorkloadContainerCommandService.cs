using AgentDeck.Core.Models;

namespace AgentDeck.Core.Services;

/// <summary>Resolves Docker build and run commands from workload definitions.</summary>
public interface IWorkloadContainerCommandService
{
    WorkloadContainerCommandSet Resolve(RunnerMachineSettings machine, WorkloadDefinition workload);
}
