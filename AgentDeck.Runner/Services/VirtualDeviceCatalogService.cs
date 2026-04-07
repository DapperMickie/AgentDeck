using System.Diagnostics;
using System.Text.Json;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

/// <inheritdoc />
public sealed class VirtualDeviceCatalogService : IVirtualDeviceCatalogService
{
    private readonly RunnerHostPlatform _hostPlatform = DetectHostPlatform();
    private readonly ILogger<VirtualDeviceCatalogService> _logger;

    public VirtualDeviceCatalogService(ILogger<VirtualDeviceCatalogService> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<VirtualDeviceCatalogSnapshot>> GetCatalogsAsync(CancellationToken cancellationToken = default)
    {
        var catalogs = VirtualDeviceCatalog.GetCatalogs(_hostPlatform);
        var results = new List<VirtualDeviceCatalogSnapshot>(catalogs.Count);

        foreach (var catalog in catalogs)
        {
            results.Add(await PopulateCatalogAsync(catalog, cancellationToken));
        }

        return results;
    }

    public async Task<VirtualDeviceCatalogSnapshot?> GetCatalogAsync(VirtualDeviceCatalogKind catalogKind, CancellationToken cancellationToken = default)
    {
        var catalog = VirtualDeviceCatalog.GetCatalogs(_hostPlatform)
            .FirstOrDefault(snapshot => snapshot.CatalogKind == catalogKind);

        return catalog is null ? null : await PopulateCatalogAsync(catalog, cancellationToken);
    }

    public async Task<VirtualDeviceLaunchResolution> ResolveSelectionAsync(VirtualDeviceLaunchSelection selection, CancellationToken cancellationToken = default)
    {
        var catalog = await GetCatalogAsync(selection.CatalogKind, cancellationToken);
        if (catalog is null)
        {
            return new VirtualDeviceLaunchResolution
            {
                Selection = CloneSelection(selection),
                CanLaunch = false,
                Message = $"Catalog '{selection.CatalogKind}' is not available on {_hostPlatform}."
            };
        }

        var resolvedDevice = string.IsNullOrWhiteSpace(selection.DeviceId)
            ? null
            : catalog.Devices.FirstOrDefault(device => device.Id.Equals(selection.DeviceId, StringComparison.OrdinalIgnoreCase));

        var resolvedProfileId = !string.IsNullOrWhiteSpace(selection.ProfileId)
            ? selection.ProfileId
            : resolvedDevice?.ProfileId;

        var resolvedProfile = string.IsNullOrWhiteSpace(resolvedProfileId)
            ? null
            : catalog.Profiles.FirstOrDefault(profile => profile.Id.Equals(resolvedProfileId, StringComparison.OrdinalIgnoreCase));

        if (resolvedDevice is null && selection.ReuseRunningDevice && resolvedProfile is not null)
        {
            resolvedDevice = catalog.Devices
                .Where(device => device.ProfileId.Equals(resolvedProfile.Id, StringComparison.OrdinalIgnoreCase) && device.IsAvailableForLaunch)
                .OrderByDescending(device => device.State == VirtualDeviceState.Running)
                .ThenByDescending(device => device.State == VirtualDeviceState.Available)
                .ThenBy(device => device.DisplayName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        var canLaunch = resolvedDevice?.IsAvailableForLaunch == true || resolvedProfile is not null;
        var message = resolvedDevice is not null
            ? $"Resolved to device '{resolvedDevice.DisplayName}'."
            : resolvedProfile is not null
                ? $"Resolved to profile '{resolvedProfile.DisplayName}'."
                : catalog.Message ?? "No matching device or profile was found.";

        return new VirtualDeviceLaunchResolution
        {
            Selection = CloneSelection(selection),
            Profile = resolvedProfile is null ? null : CloneProfile(resolvedProfile),
            Device = resolvedDevice is null ? null : CloneDevice(resolvedDevice),
            CanLaunch = canLaunch,
            Message = message
        };
    }

    private async Task<VirtualDeviceCatalogSnapshot> PopulateCatalogAsync(VirtualDeviceCatalogSnapshot catalog, CancellationToken cancellationToken)
    {
        return catalog.CatalogKind switch
        {
            VirtualDeviceCatalogKind.AndroidEmulator => await GetAndroidCatalogAsync(catalog, cancellationToken),
            VirtualDeviceCatalogKind.AppleSimulator => await GetAppleSimulatorCatalogAsync(catalog, cancellationToken),
            _ => CloneSnapshot(catalog)
        };
    }

    private async Task<VirtualDeviceCatalogSnapshot> GetAndroidCatalogAsync(VirtualDeviceCatalogSnapshot template, CancellationToken cancellationToken)
    {
        var emulatorPath = FindAndroidTool("emulator");
        var adbPath = FindAndroidTool("adb");

        var avdNames = new List<string>();
        string? discoveryMessage = null;
        var discoverySupported = false;

        if (emulatorPath is not null)
        {
            var avdResult = await RunCommandAsync(emulatorPath, ["-list-avds"], cancellationToken);
            if (avdResult.Succeeded)
            {
                avdNames = avdResult.StandardOutput
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                discoverySupported = true;
            }
            else
            {
                discoveryMessage = FirstMeaningfulLine(avdResult.StandardError, avdResult.StandardOutput);
            }
        }

        var devices = new List<VirtualDeviceInstance>();
        var runningAvdNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (adbPath is not null)
        {
            var adbResult = await RunCommandAsync(adbPath, ["devices", "-l"], cancellationToken);
            if (adbResult.Succeeded)
            {
                discoverySupported = true;
                foreach (var device in await ParseAndroidDevicesAsync(adbPath, adbResult.StandardOutput, cancellationToken))
                {
                    devices.Add(device);
                    if (!string.IsNullOrWhiteSpace(device.DisplayName))
                    {
                        runningAvdNames.Add(device.DisplayName);
                    }
                }
            }
            else
            {
                discoveryMessage ??= FirstMeaningfulLine(adbResult.StandardError, adbResult.StandardOutput);
            }
        }

        foreach (var avdName in avdNames.Where(name => !runningAvdNames.Contains(name)))
        {
            devices.Add(new VirtualDeviceInstance
            {
                Id = avdName,
                ProfileId = MapAndroidProfileId(avdName),
                CatalogKind = VirtualDeviceCatalogKind.AndroidEmulator,
                TargetPlatform = ApplicationTargetPlatform.Android,
                DisplayName = avdName,
                State = VirtualDeviceState.Available,
                IsAvailableForLaunch = true,
                Notes = "Detected via emulator -list-avds."
            });
        }

        discoveryMessage ??= discoverySupported
            ? $"Discovered {devices.Count} Android virtual device(s)."
            : "Android emulator tools were not detected on this runner.";

        return new VirtualDeviceCatalogSnapshot
        {
            CatalogKind = template.CatalogKind,
            HostPlatform = template.HostPlatform,
            CapturedAt = DateTimeOffset.UtcNow,
            DiscoverySupported = discoverySupported,
            Message = discoveryMessage,
            Profiles = template.Profiles.Select(CloneProfile).ToArray(),
            Devices = devices
                .OrderByDescending(device => device.State == VirtualDeviceState.Running)
                .ThenBy(device => device.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(CloneDevice)
                .ToArray()
        };
    }

    private async Task<VirtualDeviceCatalogSnapshot> GetAppleSimulatorCatalogAsync(VirtualDeviceCatalogSnapshot template, CancellationToken cancellationToken)
    {
        if (_hostPlatform != RunnerHostPlatform.MacOS)
        {
            return new VirtualDeviceCatalogSnapshot
            {
                CatalogKind = template.CatalogKind,
                HostPlatform = template.HostPlatform,
                CapturedAt = DateTimeOffset.UtcNow,
                DiscoverySupported = false,
                Message = "Apple simulator discovery is only available on macOS runners.",
                Profiles = template.Profiles.Select(CloneProfile).ToArray(),
                Devices = []
            };
        }

        var simctlResult = await RunCommandAsync("xcrun", ["simctl", "list", "devices", "available", "--json"], cancellationToken);
        if (!simctlResult.Succeeded)
        {
            return new VirtualDeviceCatalogSnapshot
            {
                CatalogKind = template.CatalogKind,
                HostPlatform = template.HostPlatform,
                CapturedAt = DateTimeOffset.UtcNow,
                DiscoverySupported = false,
                Message = FirstMeaningfulLine(simctlResult.StandardError, simctlResult.StandardOutput) ?? "Apple simulator tools were not detected on this runner.",
                Profiles = template.Profiles.Select(CloneProfile).ToArray(),
                Devices = []
            };
        }

        var devices = ParseAppleSimulators(simctlResult.StandardOutput);
        return new VirtualDeviceCatalogSnapshot
        {
            CatalogKind = template.CatalogKind,
            HostPlatform = template.HostPlatform,
            CapturedAt = DateTimeOffset.UtcNow,
            DiscoverySupported = true,
            Message = $"Discovered {devices.Count} Apple simulator(s).",
            Profiles = template.Profiles.Select(CloneProfile).ToArray(),
            Devices = devices
                .OrderByDescending(device => device.State == VirtualDeviceState.Running)
                .ThenBy(device => device.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(CloneDevice)
                .ToArray()
        };
    }

    private async Task<IReadOnlyList<VirtualDeviceInstance>> ParseAndroidDevicesAsync(string adbPath, string output, CancellationToken cancellationToken)
    {
        var devices = new List<VirtualDeviceInstance>();
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length < 2)
            {
                continue;
            }

            var serial = tokens[0];
            var stateToken = tokens[1];
            if (!serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var avdName = await TryGetAndroidAvdNameAsync(adbPath, serial, cancellationToken) ?? serial;
            devices.Add(new VirtualDeviceInstance
            {
                Id = serial,
                ProfileId = MapAndroidProfileId(avdName),
                CatalogKind = VirtualDeviceCatalogKind.AndroidEmulator,
                TargetPlatform = ApplicationTargetPlatform.Android,
                DisplayName = avdName,
                State = MapAndroidState(stateToken),
                IsAvailableForLaunch = stateToken.Equals("device", StringComparison.OrdinalIgnoreCase),
                Notes = $"Detected via adb ({serial})."
            });
        }

        return devices;
    }

    private static IReadOnlyList<VirtualDeviceInstance> ParseAppleSimulators(string output)
    {
        using var document = JsonDocument.Parse(output);
        var devices = new List<VirtualDeviceInstance>();

        if (!document.RootElement.TryGetProperty("devices", out var devicesElement))
        {
            return devices;
        }

        foreach (var runtime in devicesElement.EnumerateObject())
        {
            foreach (var device in runtime.Value.EnumerateArray())
            {
                if (device.TryGetProperty("isAvailable", out var isAvailable) && !isAvailable.GetBoolean())
                {
                    continue;
                }

                var name = device.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() ?? string.Empty : string.Empty;
                var udid = device.TryGetProperty("udid", out var udidProperty) ? udidProperty.GetString() ?? string.Empty : string.Empty;
                var state = device.TryGetProperty("state", out var stateProperty) ? stateProperty.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(udid))
                {
                    continue;
                }

                devices.Add(new VirtualDeviceInstance
                {
                    Id = udid,
                    ProfileId = MapAppleProfileId(name),
                    CatalogKind = VirtualDeviceCatalogKind.AppleSimulator,
                    TargetPlatform = ApplicationTargetPlatform.iOS,
                    DisplayName = name,
                    State = MapAppleState(state),
                    IsAvailableForLaunch = state.Equals("Booted", StringComparison.OrdinalIgnoreCase)
                        || state.Equals("Shutdown", StringComparison.OrdinalIgnoreCase),
                    Notes = $"Runtime: {runtime.Name}"
                });
            }
        }

        return devices;
    }

    private async Task<string?> TryGetAndroidAvdNameAsync(string adbPath, string serial, CancellationToken cancellationToken)
    {
        var result = await RunCommandAsync(adbPath, ["-s", serial, "emu", "avd", "name"], cancellationToken);
        return result.Succeeded
            ? FirstMeaningfulLine(result.StandardOutput)
            : null;
    }

    private static VirtualDeviceState MapAndroidState(string state) => state.ToLowerInvariant() switch
    {
        "device" => VirtualDeviceState.Running,
        "offline" => VirtualDeviceState.Unavailable,
        "unauthorized" => VirtualDeviceState.Busy,
        _ => VirtualDeviceState.Unknown
    };

    private static VirtualDeviceState MapAppleState(string state) => state.ToLowerInvariant() switch
    {
        "booted" => VirtualDeviceState.Running,
        "shutdown" => VirtualDeviceState.Available,
        "booting" => VirtualDeviceState.Booting,
        "creating" => VirtualDeviceState.Booting,
        "shutting down" => VirtualDeviceState.Busy,
        _ => VirtualDeviceState.Unknown
    };

    private static string MapAndroidProfileId(string deviceName) =>
        deviceName.Contains("tablet", StringComparison.OrdinalIgnoreCase) || deviceName.Contains("tab", StringComparison.OrdinalIgnoreCase)
            ? "android-tablet-default"
            : "android-phone-default";

    private static string MapAppleProfileId(string deviceName) =>
        deviceName.Contains("ipad", StringComparison.OrdinalIgnoreCase)
            ? "apple-ipad-default"
            : "apple-iphone-default";

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

    private string? FindAndroidTool(string toolName)
    {
        var candidateNames = OperatingSystem.IsWindows()
            ? new[] { $"{toolName}.exe", $"{toolName}.bat", toolName }
            : new[] { toolName };

        foreach (var root in new[]
                 {
                     Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
                     Environment.GetEnvironmentVariable("ANDROID_HOME")
                 }.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            foreach (var relative in toolName.Equals("emulator", StringComparison.OrdinalIgnoreCase)
                         ? new[] { "emulator", Path.Combine("tools", "emulator") }
                         : new[] { "platform-tools" })
            {
                foreach (var candidateName in candidateNames)
                {
                    var path = Path.Combine(root!, relative, candidateName);
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
            }
        }

        var pathEnvironment = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnvironment))
        {
            return null;
        }

        foreach (var directory in pathEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidateName in candidateNames)
            {
                var path = Path.Combine(directory, candidateName);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private async Task<ProbeResult> RunCommandAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "Virtual device probe failed to start: {FileName}", fileName);
            return new ProbeResult
            {
                StartFailed = true,
                ErrorMessage = ex.Message
            };
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new ProbeResult
        {
            Succeeded = process.ExitCode == 0,
            StandardOutput = await standardOutputTask,
            StandardError = await standardErrorTask,
            ErrorMessage = process.ExitCode == 0 ? null : $"Command exited with code {process.ExitCode}."
        };
    }

    private static string? FirstMeaningfulLine(params string[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var line = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(line))
            {
                return line;
            }
        }

        return null;
    }

    private static VirtualDeviceCatalogSnapshot CloneSnapshot(VirtualDeviceCatalogSnapshot snapshot)
    {
        return new VirtualDeviceCatalogSnapshot
        {
            CatalogKind = snapshot.CatalogKind,
            HostPlatform = snapshot.HostPlatform,
            CapturedAt = snapshot.CapturedAt,
            DiscoverySupported = snapshot.DiscoverySupported,
            Message = snapshot.Message,
            Profiles = snapshot.Profiles.Select(CloneProfile).ToArray(),
            Devices = snapshot.Devices.Select(CloneDevice).ToArray()
        };
    }

    private static VirtualDeviceProfile CloneProfile(VirtualDeviceProfile profile)
    {
        return new VirtualDeviceProfile
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
        };
    }

    private static VirtualDeviceInstance CloneDevice(VirtualDeviceInstance device)
    {
        return new VirtualDeviceInstance
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
        };
    }

    private static VirtualDeviceLaunchSelection CloneSelection(VirtualDeviceLaunchSelection selection)
    {
        return new VirtualDeviceLaunchSelection
        {
            CatalogKind = selection.CatalogKind,
            TargetPlatform = selection.TargetPlatform,
            DeviceId = selection.DeviceId,
            ProfileId = selection.ProfileId,
            DisplayName = selection.DisplayName,
            StartBeforeLaunch = selection.StartBeforeLaunch,
            ReuseRunningDevice = selection.ReuseRunningDevice
        };
    }

    private sealed class ProbeResult
    {
        public bool Succeeded { get; init; }
        public bool StartFailed { get; init; }
        public string StandardOutput { get; init; } = string.Empty;
        public string StandardError { get; init; } = string.Empty;
        public string? ErrorMessage { get; init; }
    }
}
