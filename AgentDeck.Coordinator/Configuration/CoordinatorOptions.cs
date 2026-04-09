namespace AgentDeck.Coordinator.Configuration;

public sealed class CoordinatorOptions
{
    public const string SectionName = "Coordinator";

    public int Port { get; set; } = 5001;

    public TimeSpan WorkerHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan WorkerExpiry { get; set; } = TimeSpan.FromSeconds(45);
}
