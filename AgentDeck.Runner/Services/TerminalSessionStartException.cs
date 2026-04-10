using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

public sealed class TerminalSessionStartException : Exception
{
    public TerminalSessionStartException(TerminalSession session, Exception innerException)
        : base(innerException.Message, innerException)
    {
        Session = session;
    }

    public TerminalSession Session { get; }
}
