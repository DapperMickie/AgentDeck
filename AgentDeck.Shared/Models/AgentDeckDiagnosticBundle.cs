namespace AgentDeck.Shared.Models;

/// <summary>Redacted local diagnostic snapshot for troubleshooting an AgentDeck component.</summary>
public sealed record AgentDeckDiagnosticBundle
{
    public required string Component { get; init; }
    public required string GeneratedAt { get; init; }
    public required string MachineName { get; init; }
    public required string OperatingSystem { get; init; }
    public required string ProcessArchitecture { get; init; }
    public required string FrameworkDescription { get; init; }
    public IReadOnlyDictionary<string, object?> Configuration { get; init; } = new Dictionary<string, object?>();
    public IReadOnlyDictionary<string, object?> State { get; init; } = new Dictionary<string, object?>();
    public IReadOnlyList<string> KnownLimitations { get; init; } = [];
}
