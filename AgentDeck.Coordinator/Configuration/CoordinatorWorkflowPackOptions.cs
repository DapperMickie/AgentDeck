namespace AgentDeck.Coordinator.Configuration;

public sealed class CoordinatorWorkflowPackOptions
{
    public string PackId { get; set; } = "default-machine-setup";
    public string Version { get; set; } = "1";
    public string DisplayName { get; set; } = "Default Machine Setup";
    public string? Description { get; set; } = "First-pass coordinator-defined machine setup workflow pack.";
    public List<CoordinatorWorkflowStepOptions> Steps { get; set; } = [];
}
