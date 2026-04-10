using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

/// <inheritdoc />
public sealed partial class MachineCapabilityService : IMachineCapabilityService
{
    private readonly IRunnerCapabilityCatalogService _capabilityCatalog;
    private readonly ILogger<MachineCapabilityService> _logger;
    private readonly IVirtualDeviceCatalogService _virtualDevices;
    private readonly IRemoteViewerSessionService _viewers;

    public MachineCapabilityService(
        IRunnerCapabilityCatalogService capabilityCatalog,
        IVirtualDeviceCatalogService virtualDevices,
        IRemoteViewerSessionService viewers,
        ILogger<MachineCapabilityService> logger)
    {
        _capabilityCatalog = capabilityCatalog;
        _virtualDevices = virtualDevices;
        _viewers = viewers;
        _logger = logger;
    }

    public async Task<MachineCapabilitiesSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var catalog = await _capabilityCatalog.GetCurrentCatalogAsync(cancellationToken);
        var capabilities = catalog is null
            ? []
            : await DetectCapabilitiesAsync(catalog, cancellationToken);
        var catalogs = await _virtualDevices.GetCatalogsAsync(cancellationToken);

        return new MachineCapabilitiesSnapshot
        {
            CapturedAt = DateTimeOffset.UtcNow,
            Platform = BuildPlatformProfile(),
            SupportedTargets = BuildSupportedTargets(capabilities, catalogs),
            RemoteViewerProviders = _viewers.GetAvailableProviders(),
            Capabilities = capabilities
        };
    }

    private async Task<IReadOnlyList<MachineCapability>> DetectCapabilitiesAsync(
        RunnerCapabilityCatalog catalog,
        CancellationToken cancellationToken)
    {
        var capabilities = new List<MachineCapability>(catalog.Capabilities.Count);
        foreach (var definition in catalog.Capabilities)
        {
            capabilities.Add(await DetectCapabilityAsync(definition, cancellationToken));
        }

        return capabilities;
    }

    private static MachinePlatformProfile BuildPlatformProfile()
    {
        return new MachinePlatformProfile
        {
            HostPlatform = GetHostPlatform(),
            OperatingSystemDescription = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.OSArchitecture.ToString()
        };
    }

    private static IReadOnlyList<MachineTargetSupport> BuildSupportedTargets(
        IReadOnlyList<MachineCapability> capabilities,
        IReadOnlyList<VirtualDeviceCatalogSnapshot> catalogs)
    {
        var dotNetInstalled = capabilities.Any(capability =>
            capability.Id.Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
            capability.Status == MachineCapabilityStatus.Installed);
        var supportedTargets = new List<MachineTargetSupport>();

        if (OperatingSystem.IsLinux())
        {
            supportedTargets.Add(CreateTargetSupport(ApplicationTargetPlatform.Linux, dotNetInstalled));
        }
        else if (OperatingSystem.IsWindows())
        {
            supportedTargets.Add(CreateTargetSupport(ApplicationTargetPlatform.Windows, dotNetInstalled));
        }
        else if (OperatingSystem.IsMacOS())
        {
            supportedTargets.Add(CreateTargetSupport(ApplicationTargetPlatform.MacOS, dotNetInstalled));
        }

        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            supportedTargets.Add(CreateDeviceBackedTargetSupport(
                ApplicationTargetPlatform.Android,
                dotNetInstalled,
                catalogs,
                VirtualDeviceCatalogKind.AndroidEmulator));
        }

        if (OperatingSystem.IsMacOS())
        {
            supportedTargets.Add(CreateDeviceBackedTargetSupport(
                ApplicationTargetPlatform.iOS,
                dotNetInstalled,
                catalogs,
                VirtualDeviceCatalogKind.AppleSimulator));
        }

        return supportedTargets;
    }

    private static MachineTargetSupport CreateTargetSupport(ApplicationTargetPlatform platform, bool dotNetInstalled)
    {
        var definition = ProjectTargetCatalog.GetTargets(ProjectWorkloadKind.Maui)
            .First(target => target.Platform == platform);

        var requiredCapabilities = string.IsNullOrWhiteSpace(definition.CapabilityId)
            ? []
            : new[] { definition.CapabilityId };

        var notes = definition.Notes;
        if (!string.IsNullOrWhiteSpace(definition.PackageRequirement))
        {
            notes = string.IsNullOrWhiteSpace(notes)
                ? $"Requires {definition.PackageRequirement}."
                : $"{notes} Requires {definition.PackageRequirement}.";
        }

        if (!dotNetInstalled)
        {
            notes = string.IsNullOrWhiteSpace(notes)
                ? "Install the .NET SDK first."
                : $"Install the .NET SDK first. {notes}";
        }

        return new MachineTargetSupport
        {
            Platform = definition.Platform,
            Status = dotNetInstalled ? MachineTargetSupportStatus.Supported : MachineTargetSupportStatus.RequiresSetup,
            DisplayName = definition.DisplayName,
            RequiredCapabilities = requiredCapabilities,
            Notes = notes
        };
    }

    private static MachineTargetSupport CreateDeviceBackedTargetSupport(
        ApplicationTargetPlatform platform,
        bool dotNetInstalled,
        IReadOnlyList<VirtualDeviceCatalogSnapshot> catalogs,
        VirtualDeviceCatalogKind catalogKind)
    {
        var definition = ProjectTargetCatalog.GetTargets(ProjectWorkloadKind.Maui)
            .First(target => target.Platform == platform);
        var catalog = catalogs.FirstOrDefault(candidate => candidate.CatalogKind == catalogKind);
        var requiredCapabilities = string.IsNullOrWhiteSpace(definition.CapabilityId)
            ? []
            : new[] { definition.CapabilityId };
        var notes = definition.Notes;

        if (!string.IsNullOrWhiteSpace(catalog?.Message))
        {
            notes = string.IsNullOrWhiteSpace(notes)
                ? catalog.Message
                : $"{notes} {catalog.Message}";
        }

        var status = MachineTargetSupportStatus.Supported;
        if (!dotNetInstalled || catalog is null || !catalog.DiscoverySupported)
        {
            status = MachineTargetSupportStatus.RequiresSetup;
        }

        if (!dotNetInstalled)
        {
            notes = string.IsNullOrWhiteSpace(notes)
                ? "Install the .NET SDK first."
                : $"Install the .NET SDK first. {notes}";
        }

        return new MachineTargetSupport
        {
            Platform = definition.Platform,
            Status = status,
            DisplayName = definition.DisplayName,
            RequiredCapabilities = requiredCapabilities,
            DeviceCatalog = catalogKind,
            RequiresDeviceSelection = true,
            AvailableDeviceCount = catalog?.Devices.Count ?? 0,
            AvailableDeviceProfileCount = catalog?.Profiles.Count ?? 0,
            Notes = notes
        };
    }

    private static RunnerHostPlatform GetHostPlatform()
    {
        if (OperatingSystem.IsLinux())
        {
            return RunnerHostPlatform.Linux;
        }

        if (OperatingSystem.IsWindows())
        {
            return RunnerHostPlatform.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return RunnerHostPlatform.MacOS;
        }

        return RunnerHostPlatform.Unknown;
    }

    private async Task<MachineCapability> DetectCapabilityAsync(
        RunnerCapabilityDefinition definition,
        CancellationToken cancellationToken)
    {
        return definition.ProbeKind switch
        {
            RunnerCapabilityProbeKind.GenericCliVersion => await DetectCliAsync(definition, definition.ProbeCommands, ParseFirstLineVersion, cancellationToken),
            RunnerCapabilityProbeKind.NodeVersion => await DetectSingleCommandVersionAsync(definition, ParseTrimmedOutput, "Detected via {0}", cancellationToken),
            RunnerCapabilityProbeKind.PythonVersion => await DetectPythonAsync(definition, cancellationToken),
            RunnerCapabilityProbeKind.DotNetSdk => await DetectDotNetAsync(definition, cancellationToken),
            _ => CreateErrorCapability(definition.CapabilityId, definition.DisplayName, definition.Category, $"Unsupported probe kind '{definition.ProbeKind}'.")
        };
    }

    private async Task<MachineCapability> DetectCliAsync(
        RunnerCapabilityDefinition definition,
        IReadOnlyList<RunnerCapabilityProbeCommand> commands,
        Func<string, string, string?> versionParser,
        CancellationToken cancellationToken)
    {
        if (commands.Count == 0)
        {
            return CreateErrorCapability(definition.CapabilityId, definition.DisplayName, definition.Category, "Capability catalog did not define any probe commands.");
        }

        string? lastError = null;
        var sawExecutionFailure = false;

        foreach (var command in commands)
        {
            var result = await RunCommandAsync(command.FileName, command.Arguments, cancellationToken);
            if (result.StartFailed)
            {
                lastError = result.ErrorMessage;
                continue;
            }

            if (!result.Succeeded)
            {
                sawExecutionFailure = true;
                lastError = FirstMeaningfulLine(result.StandardError, result.StandardOutput) ?? "Detection failed.";
                continue;
            }

            var version = versionParser(result.StandardOutput, result.StandardError);
            var installedVersions = string.IsNullOrWhiteSpace(version) ? [] : new[] { version };
            return new MachineCapability
            {
                Id = definition.CapabilityId,
                Name = definition.DisplayName,
                Category = definition.Category,
                Status = MachineCapabilityStatus.Installed,
                Version = version,
                InstalledVersions = installedVersions,
                Message = $"Detected via {command.FileName}"
            };
        }

        return new MachineCapability
        {
            Id = definition.CapabilityId,
            Name = definition.DisplayName,
            Category = definition.Category,
            Status = sawExecutionFailure ? MachineCapabilityStatus.Error : MachineCapabilityStatus.Missing,
            Message = lastError ?? "Not installed."
        };
    }

    private async Task<MachineCapability> DetectSingleCommandVersionAsync(
        RunnerCapabilityDefinition definition,
        Func<string, string, string?> versionParser,
        string successMessageFormat,
        CancellationToken cancellationToken)
    {
        var command = definition.ProbeCommands.FirstOrDefault();
        if (command is null)
        {
            return CreateErrorCapability(definition.CapabilityId, definition.DisplayName, definition.Category, "Capability catalog did not define any probe commands.");
        }

        var result = await RunCommandAsync(command.FileName, command.Arguments, cancellationToken);
        if (result.StartFailed)
        {
            return CreateMissingCapability(definition.CapabilityId, definition.DisplayName, definition.Category, result.ErrorMessage);
        }

        if (!result.Succeeded)
        {
            return CreateErrorCapability(definition.CapabilityId, definition.DisplayName, definition.Category, result.StandardError, result.StandardOutput);
        }

        var version = NormalizeVersion(versionParser(result.StandardOutput, result.StandardError));
        return new MachineCapability
        {
            Id = definition.CapabilityId,
            Name = definition.DisplayName,
            Category = definition.Category,
            Status = MachineCapabilityStatus.Installed,
            Version = version,
            InstalledVersions = version is null ? [] : [version],
            Message = string.Format(successMessageFormat, command.FileName)
        };
    }

    private async Task<MachineCapability> DetectPythonAsync(RunnerCapabilityDefinition definition, CancellationToken cancellationToken)
    {
        if (definition.ProbeCommands.Count == 0)
        {
            return CreateErrorCapability(definition.CapabilityId, definition.DisplayName, definition.Category, "Capability catalog did not define any probe commands.");
        }

        var installedVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? primaryVersion = null;
        string? lastError = null;
        var sawExecutionFailure = false;

        foreach (var command in definition.ProbeCommands)
        {
            var result = await RunCommandAsync(command.FileName, command.Arguments, cancellationToken);
            if (result.StartFailed)
            {
                lastError = result.ErrorMessage;
                continue;
            }

            if (!result.Succeeded)
            {
                sawExecutionFailure = true;
                lastError = FirstMeaningfulLine(result.StandardError, result.StandardOutput) ?? "Detection failed.";
                continue;
            }

            var version = NormalizeVersion(ParseTrimmedOutput(result.StandardOutput, result.StandardError));
            if (!string.IsNullOrWhiteSpace(version))
            {
                installedVersions.Add(version);
                primaryVersion ??= version;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            var pyLauncherResult = await RunCommandAsync("py", ["-0p"], cancellationToken);
            if (!pyLauncherResult.StartFailed && pyLauncherResult.Succeeded)
            {
                foreach (var version in ParseVersions(pyLauncherResult.StandardOutput))
                {
                    installedVersions.Add(version);
                    primaryVersion ??= version;
                }
            }
        }
        else
        {
            foreach (var command in FindCommandsMatching(PythonCommandPattern()))
            {
                var result = await RunCommandAsync(command, ["--version"], cancellationToken);
                if (result.StartFailed || !result.Succeeded)
                {
                    continue;
                }

                var version = NormalizeVersion(ParseTrimmedOutput(result.StandardOutput, result.StandardError));
                if (!string.IsNullOrWhiteSpace(version))
                {
                    installedVersions.Add(version);
                    primaryVersion ??= version;
                }
            }
        }

        if (installedVersions.Count > 0)
        {
            var sortedVersions = SortVersionsDescending(installedVersions);
            return new MachineCapability
            {
                Id = definition.CapabilityId,
                Name = definition.DisplayName,
                Category = definition.Category,
                Status = MachineCapabilityStatus.Installed,
                Version = primaryVersion ?? sortedVersions.FirstOrDefault(),
                InstalledVersions = sortedVersions,
                Message = sortedVersions.Count > 1
                    ? "Multiple Python versions detected."
                    : OperatingSystem.IsWindows() ? "Detected via Python launcher." : "Detected via python/python3."
            };
        }

        return new MachineCapability
        {
            Id = definition.CapabilityId,
            Name = definition.DisplayName,
            Category = definition.Category,
            Status = sawExecutionFailure ? MachineCapabilityStatus.Error : MachineCapabilityStatus.Missing,
            Message = lastError ?? "Not installed."
        };
    }

    private async Task<MachineCapability> DetectDotNetAsync(RunnerCapabilityDefinition definition, CancellationToken cancellationToken)
    {
        var commands = definition.ProbeCommands;
        if (commands.Count == 0)
        {
            return CreateErrorCapability(definition.CapabilityId, definition.DisplayName, definition.Category, "Capability catalog did not define any probe commands.");
        }

        var currentCommand = commands[0];
        var currentResult = await RunCommandAsync(currentCommand.FileName, currentCommand.Arguments, cancellationToken);
        if (currentResult.StartFailed)
        {
            return CreateMissingCapability(definition.CapabilityId, definition.DisplayName, definition.Category, currentResult.ErrorMessage);
        }

        if (!currentResult.Succeeded)
        {
            return CreateErrorCapability(definition.CapabilityId, definition.DisplayName, definition.Category, currentResult.StandardError, currentResult.StandardOutput);
        }

        var currentVersion = NormalizeVersion(ParseTrimmedOutput(currentResult.StandardOutput, currentResult.StandardError));
        var installedVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(currentVersion))
        {
            installedVersions.Add(currentVersion);
        }

        if (commands.Count > 1)
        {
            var listCommand = commands[1];
            var listResult = await RunCommandAsync(listCommand.FileName, listCommand.Arguments, cancellationToken);
            if (!listResult.StartFailed && listResult.Succeeded)
            {
                foreach (var version in ParseVersions(listResult.StandardOutput))
                {
                    installedVersions.Add(version);
                }
            }
        }

        var sortedVersions = SortVersionsDescending(installedVersions);
        return new MachineCapability
        {
            Id = definition.CapabilityId,
            Name = definition.DisplayName,
            Category = definition.Category,
            Status = MachineCapabilityStatus.Installed,
            Version = currentVersion ?? sortedVersions.FirstOrDefault(),
            InstalledVersions = sortedVersions,
            Message = sortedVersions.Count > 1 ? "Multiple .NET SDK versions detected." : "Detected via dotnet"
        };
    }

    private MachineCapability CreateMissingCapability(string id, string name, string category, string? errorMessage)
    {
        return new MachineCapability
        {
            Id = id,
            Name = name,
            Category = category,
            Status = MachineCapabilityStatus.Missing,
            Message = errorMessage ?? "Not installed."
        };
    }

    private MachineCapability CreateErrorCapability(string id, string name, string category, params string[] messages)
    {
        return new MachineCapability
        {
            Id = id,
            Name = name,
            Category = category,
            Status = MachineCapabilityStatus.Error,
            Message = FirstMeaningfulLine(messages) ?? "Detection failed."
        };
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
            return new ProbeResult
            {
                StartFailed = true,
                ErrorMessage = ex.Message
            };
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;

        if (process.ExitCode != 0)
        {
            _logger.LogDebug("Capability probe {FileName} exited with {ExitCode}: {Error}", fileName, process.ExitCode, standardError);
        }

        return new ProbeResult
        {
            Succeeded = process.ExitCode == 0,
            StandardOutput = standardOutput,
            StandardError = standardError
        };
    }

    private static IReadOnlyList<string> FindCommandsMatching(Regex pattern)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    var fileName = Path.GetFileName(file);
                    if (pattern.IsMatch(fileName))
                    {
                        matches.Add(Path.GetFileNameWithoutExtension(file));
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return matches.ToArray();
    }

    private static IReadOnlyList<string> ParseVersions(string value)
    {
        var versions = VersionPattern()
            .Matches(value)
            .Select(match => NormalizeVersion(match.Value))
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return SortVersionsDescending(versions);
    }

    private static IReadOnlyList<string> SortVersionsDescending(IEnumerable<string> versions)
    {
        return versions
            .Select(version => version.Trim())
            .Where(version => version.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(version => ParseVersionForSorting(version))
            .ThenByDescending(version => version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Version ParseVersionForSorting(string version)
    {
        var normalized = NormalizeVersion(version);
        return Version.TryParse(normalized, out var parsed)
            ? parsed
            : new Version(0, 0);
    }

    private static string? ParseFirstLineVersion(string standardOutput, string standardError) =>
        FirstMeaningfulLine(standardOutput, standardError);

    private static string? ParseTrimmedOutput(string standardOutput, string standardError)
    {
        var line = FirstMeaningfulLine(standardOutput, standardError);
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var match = VersionPattern().Match(line);
        return match.Success ? match.Value : line;
    }

    private static string? NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        return version.Trim().TrimStart('v', 'V');
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

    [GeneratedRegex(@"\d+(\.\d+)+")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^python(?:3\.\d+)?(?:\.exe)?$", RegexOptions.IgnoreCase)]
    private static partial Regex PythonCommandPattern();

    private sealed class ProbeResult
    {
        public bool Succeeded { get; init; }
        public bool StartFailed { get; init; }
        public string StandardOutput { get; init; } = string.Empty;
        public string StandardError { get; init; } = string.Empty;
        public string? ErrorMessage { get; init; }
    }
}
