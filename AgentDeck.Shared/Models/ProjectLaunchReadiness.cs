using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Evaluates whether a machine can expose the remote surfaces required by a launch profile.</summary>
public static class ProjectLaunchReadiness
{
    public static bool SupportsLaunchProfile(
        ProjectLaunchProfile profile,
        IReadOnlyList<MachineTargetSupport> supportedTargets,
        IReadOnlyList<RemoteViewerProviderCapability> remoteViewerProviders) =>
        SupportsPlatform(profile, supportedTargets) &&
        GetMissingViewerTargets(profile, remoteViewerProviders).Count == 0;

    public static bool SupportsPlatform(
        ProjectLaunchProfile profile,
        IReadOnlyList<MachineTargetSupport> supportedTargets) =>
        supportedTargets.Any(target =>
            target.Platform == profile.Platform &&
            target.Status == MachineTargetSupportStatus.Supported);

    public static IReadOnlyList<RemoteViewerTargetKind> GetRequiredViewerTargets(ProjectLaunchProfile profile)
    {
        var requiredTargets = new List<RemoteViewerTargetKind>();

        if (profile.RequiresVsCode)
        {
            requiredTargets.Add(RemoteViewerTargetKind.VsCode);
        }

        if (profile.RequiresEmulator)
        {
            requiredTargets.Add(RemoteViewerTargetKind.Emulator);
        }
        else if (profile.RequiresSimulator)
        {
            requiredTargets.Add(RemoteViewerTargetKind.Simulator);
        }
        else if (profile.Platform is ApplicationTargetPlatform.Windows or ApplicationTargetPlatform.Linux or ApplicationTargetPlatform.MacOS)
        {
            requiredTargets.Add(RemoteViewerTargetKind.Window);
        }

        return requiredTargets;
    }

    public static IReadOnlyList<RemoteViewerTargetKind> GetMissingViewerTargets(
        ProjectLaunchProfile profile,
        IReadOnlyList<RemoteViewerProviderCapability> remoteViewerProviders)
    {
        var supportedViewerTargets = remoteViewerProviders
            .SelectMany(provider => provider.SupportedTargets)
            .ToHashSet();

        return GetRequiredViewerTargets(profile)
            .Where(target => !supportedViewerTargets.Contains(target))
            .Distinct()
            .ToArray();
    }
}
