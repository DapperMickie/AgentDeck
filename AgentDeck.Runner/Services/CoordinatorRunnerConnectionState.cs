using Microsoft.AspNetCore.SignalR.Client;

namespace AgentDeck.Runner.Services;

public sealed class CoordinatorRunnerConnectionState
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public HubConnection? Connection { get; set; }
}
