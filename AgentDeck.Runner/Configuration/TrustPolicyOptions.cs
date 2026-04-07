namespace AgentDeck.Runner.Configuration;

/// <summary>Configures first-pass trust policy checks for privileged runner actions.</summary>
public sealed class TrustPolicyOptions
{
    public const string SectionName = "TrustPolicy";

    public string ActorHeaderName { get; set; } = "X-AgentDeck-Actor";
    public bool RequireActorHeaderForPrivilegedActions { get; set; }
    public bool RequireLoopbackForMachineSetup { get; set; }
    public bool RequireLoopbackForDesktopViewerBootstrap { get; set; }
}
