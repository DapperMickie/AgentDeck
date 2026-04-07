namespace AgentDeck.Shared.Enums;

/// <summary>Declares how a configured runner machine participates in multi-machine orchestration.</summary>
public enum RunnerMachineRole
{
    Standalone,
    Coordinator,
    Worker
}
