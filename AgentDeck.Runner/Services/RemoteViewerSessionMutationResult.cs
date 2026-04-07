using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

public sealed class RemoteViewerSessionMutationResult
{
    public required RemoteViewerSessionMutationOutcome Outcome { get; init; }
    public RemoteViewerSession? Session { get; init; }
}
