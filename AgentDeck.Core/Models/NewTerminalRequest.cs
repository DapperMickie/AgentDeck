using AgentDeck.Shared.Models;

namespace AgentDeck.Core.Models;

/// <summary>Companion-side terminal creation request including the target machine.</summary>
public sealed class NewTerminalRequest
{
    public required string MachineId { get; init; }
    public required CreateTerminalRequest Terminal { get; init; }
}
