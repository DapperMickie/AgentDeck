using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Shared catalog of default project targets that AgentDeck can orchestrate.</summary>
public static class ProjectTargetCatalog
{
    private static readonly IReadOnlyList<ProjectTargetDefinition> BlazorTargets =
    [
        new()
        {
            Workload = ProjectWorkloadKind.Blazor,
            Platform = ApplicationTargetPlatform.Linux,
            DisplayName = "Blazor on Linux",
            CapabilityId = "dotnet",
            RequiresVsCodeForDebugging = true,
            Notes = "Run the Blazor app on a Linux host and debug through VS Code when needed."
        },
        new()
        {
            Workload = ProjectWorkloadKind.Blazor,
            Platform = ApplicationTargetPlatform.Windows,
            DisplayName = "Blazor on Windows",
            CapabilityId = "dotnet",
            RequiresVsCodeForDebugging = true,
            Notes = "Run the Blazor app on a Windows host and debug through VS Code when needed."
        },
        new()
        {
            Workload = ProjectWorkloadKind.Blazor,
            Platform = ApplicationTargetPlatform.MacOS,
            DisplayName = "Blazor on macOS",
            CapabilityId = "dotnet",
            RequiresVsCodeForDebugging = true,
            Notes = "Run the Blazor app on a macOS host and debug through VS Code when needed."
        }
    ];

    private static readonly IReadOnlyList<ProjectTargetDefinition> MauiTargets =
    [
        new()
        {
            Workload = ProjectWorkloadKind.Maui,
            Platform = ApplicationTargetPlatform.Linux,
            DisplayName = "Linux",
            CapabilityId = "dotnet",
            RequiresVsCodeForDebugging = true,
            PackageRequirement = "OpenMaui.Controls.Linux",
            Notes = "Use OpenMaui.Controls.Linux to enable Linux MAUI targets."
        },
        new()
        {
            Workload = ProjectWorkloadKind.Maui,
            Platform = ApplicationTargetPlatform.Windows,
            DisplayName = "Windows",
            CapabilityId = "dotnet",
            RequiresVsCodeForDebugging = true,
            Notes = ".NET MAUI Windows targets can run on a Windows worker."
        },
        new()
        {
            Workload = ProjectWorkloadKind.Maui,
            Platform = ApplicationTargetPlatform.MacOS,
            DisplayName = "macOS",
            CapabilityId = "dotnet",
            RequiresVsCodeForDebugging = true,
            Notes = ".NET MAUI macOS targets can run on a macOS worker."
        },
        new()
        {
            Workload = ProjectWorkloadKind.Maui,
            Platform = ApplicationTargetPlatform.iOS,
            DisplayName = "iOS",
            CapabilityId = "dotnet",
            RequiresVsCodeForDebugging = true,
            Notes = "iOS targets require a macOS worker with Apple tooling and simulator support."
        },
        new()
        {
            Workload = ProjectWorkloadKind.Maui,
            Platform = ApplicationTargetPlatform.Android,
            DisplayName = "Android",
            CapabilityId = "dotnet",
            RequiresVsCodeForDebugging = true,
            Notes = "Android targets require a worker with Android SDK and emulator support."
        }
    ];

    /// <summary>Gets the default targets for a specific workload.</summary>
    public static IReadOnlyList<ProjectTargetDefinition> GetTargets(ProjectWorkloadKind workload) => workload switch
    {
        ProjectWorkloadKind.Blazor => BlazorTargets,
        ProjectWorkloadKind.Maui => MauiTargets,
        _ => []
    };
}
