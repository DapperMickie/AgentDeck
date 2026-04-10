namespace AgentDeck.Shared.Enums;

/// <summary>Primitive action types that coordinator-defined workflow packs can describe.</summary>
public enum RunnerWorkflowStepKind
{
    RunCommand,
    ManageCapability,
    DownloadFile,
    ExtractArchive,
    WriteFile,
    SetEnvironmentVariable,
    VerifyInstalledTool
}
