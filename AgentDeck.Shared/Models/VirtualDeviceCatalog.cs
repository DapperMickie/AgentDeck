using AgentDeck.Shared.Enums;

namespace AgentDeck.Shared.Models;

/// <summary>Shared default catalog definitions for Android emulators and Apple simulators.</summary>
public static class VirtualDeviceCatalog
{
    private static readonly IReadOnlyList<VirtualDeviceProfile> AndroidProfiles =
    [
        new()
        {
            Id = "android-phone-default",
            CatalogKind = VirtualDeviceCatalogKind.AndroidEmulator,
            TargetPlatform = ApplicationTargetPlatform.Android,
            DisplayName = "Pixel Phone",
            FormFactor = VirtualDeviceFormFactor.Phone,
            PlatformVersion = "Android 15",
            Width = 1080,
            Height = 2400,
            IsDefault = true,
            Notes = "General-purpose Android phone emulator profile."
        },
        new()
        {
            Id = "android-tablet-default",
            CatalogKind = VirtualDeviceCatalogKind.AndroidEmulator,
            TargetPlatform = ApplicationTargetPlatform.Android,
            DisplayName = "Pixel Tablet",
            FormFactor = VirtualDeviceFormFactor.Tablet,
            PlatformVersion = "Android 15",
            Width = 1600,
            Height = 2560,
            Notes = "Tablet-oriented Android emulator profile."
        }
    ];

    private static readonly IReadOnlyList<VirtualDeviceProfile> AppleSimulatorProfiles =
    [
        new()
        {
            Id = "apple-iphone-default",
            CatalogKind = VirtualDeviceCatalogKind.AppleSimulator,
            TargetPlatform = ApplicationTargetPlatform.iOS,
            DisplayName = "iPhone Pro",
            FormFactor = VirtualDeviceFormFactor.Phone,
            PlatformVersion = "iOS 18",
            Width = 1320,
            Height = 2868,
            IsDefault = true,
            Notes = "Default iPhone simulator profile for debug and run flows."
        },
        new()
        {
            Id = "apple-ipad-default",
            CatalogKind = VirtualDeviceCatalogKind.AppleSimulator,
            TargetPlatform = ApplicationTargetPlatform.iOS,
            DisplayName = "iPad Pro",
            FormFactor = VirtualDeviceFormFactor.Tablet,
            PlatformVersion = "iPadOS 18",
            Width = 2064,
            Height = 2752,
            Notes = "Default iPad simulator profile for tablet validation."
        }
    ];

    /// <summary>Gets all profiles for a specific virtual device catalog.</summary>
    public static IReadOnlyList<VirtualDeviceProfile> GetProfiles(VirtualDeviceCatalogKind catalogKind) => catalogKind switch
    {
        VirtualDeviceCatalogKind.AndroidEmulator => AndroidProfiles,
        VirtualDeviceCatalogKind.AppleSimulator => AppleSimulatorProfiles,
        _ => []
    };

    /// <summary>Gets a catalog snapshot template for a specific runner host platform.</summary>
    public static IReadOnlyList<VirtualDeviceCatalogSnapshot> GetCatalogs(RunnerHostPlatform hostPlatform)
    {
        var catalogs = new List<VirtualDeviceCatalogSnapshot>();

        if (hostPlatform is RunnerHostPlatform.Windows or RunnerHostPlatform.Linux or RunnerHostPlatform.MacOS)
        {
            catalogs.Add(new VirtualDeviceCatalogSnapshot
            {
                CatalogKind = VirtualDeviceCatalogKind.AndroidEmulator,
                HostPlatform = hostPlatform,
                DiscoverySupported = true,
                Profiles = AndroidProfiles
            });
        }

        if (hostPlatform == RunnerHostPlatform.MacOS)
        {
            catalogs.Add(new VirtualDeviceCatalogSnapshot
            {
                CatalogKind = VirtualDeviceCatalogKind.AppleSimulator,
                HostPlatform = hostPlatform,
                DiscoverySupported = true,
                Profiles = AppleSimulatorProfiles
            });
        }

        return catalogs;
    }
}
