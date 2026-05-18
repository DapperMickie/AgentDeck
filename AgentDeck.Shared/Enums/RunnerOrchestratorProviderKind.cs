namespace AgentDeck.Shared.Enums;

/// <summary>Provider family that can create AgentDeck runner capacity on demand.</summary>
public enum RunnerOrchestratorProviderKind
{
    Portainer,
    Docker,
    Kubernetes,
    CloudVm,
    LocalLauncher,
    MacPool,
    WindowsPool
}
