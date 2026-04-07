using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

/// <inheritdoc />
public sealed class VirtualDeviceCatalogService : IVirtualDeviceCatalogService
{
    private readonly RunnerHostPlatform _hostPlatform = DetectHostPlatform();

    public IReadOnlyList<VirtualDeviceCatalogSnapshot> GetCatalogs()
    {
        return VirtualDeviceCatalog.GetCatalogs(_hostPlatform)
            .Select(CloneSnapshot)
            .ToArray();
    }

    public VirtualDeviceCatalogSnapshot? GetCatalog(VirtualDeviceCatalogKind catalogKind)
    {
        var catalog = VirtualDeviceCatalog.GetCatalogs(_hostPlatform)
            .FirstOrDefault(snapshot => snapshot.CatalogKind == catalogKind);

        return catalog is null ? null : CloneSnapshot(catalog);
    }

    private static RunnerHostPlatform DetectHostPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return RunnerHostPlatform.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return RunnerHostPlatform.MacOS;
        }

        if (OperatingSystem.IsLinux())
        {
            return RunnerHostPlatform.Linux;
        }

        return RunnerHostPlatform.Unknown;
    }

    private static VirtualDeviceCatalogSnapshot CloneSnapshot(VirtualDeviceCatalogSnapshot snapshot)
    {
        return new VirtualDeviceCatalogSnapshot
        {
            CatalogKind = snapshot.CatalogKind,
            HostPlatform = snapshot.HostPlatform,
            CapturedAt = DateTimeOffset.UtcNow,
            Profiles =
            [
                .. snapshot.Profiles.Select(profile => new VirtualDeviceProfile
                {
                    Id = profile.Id,
                    CatalogKind = profile.CatalogKind,
                    TargetPlatform = profile.TargetPlatform,
                    DisplayName = profile.DisplayName,
                    FormFactor = profile.FormFactor,
                    PlatformVersion = profile.PlatformVersion,
                    Width = profile.Width,
                    Height = profile.Height,
                    IsDefault = profile.IsDefault,
                    Notes = profile.Notes
                })
            ],
            Devices =
            [
                .. snapshot.Devices.Select(device => new VirtualDeviceInstance
                {
                    Id = device.Id,
                    ProfileId = device.ProfileId,
                    CatalogKind = device.CatalogKind,
                    TargetPlatform = device.TargetPlatform,
                    DisplayName = device.DisplayName,
                    State = device.State,
                    IsAvailableForLaunch = device.IsAvailableForLaunch,
                    MachineId = device.MachineId,
                    MachineName = device.MachineName,
                    Notes = device.Notes
                })
            ]
        };
    }
}
