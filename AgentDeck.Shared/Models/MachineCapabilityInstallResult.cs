namespace AgentDeck.Shared.Models;

/// <summary>Result of attempting to install a supported capability on a runner machine.</summary>
public sealed class MachineCapabilityInstallResult
{
    public required string CapabilityId { get; init; }
    public required string CapabilityName { get; init; }
    public string Action { get; init; } = "install";
    public string? RequestedVersion { get; init; }
    public bool Succeeded { get; init; }
    public int ExitCode { get; init; }
    public string CommandText { get; init; } = string.Empty;
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public string? Message { get; init; }
}
