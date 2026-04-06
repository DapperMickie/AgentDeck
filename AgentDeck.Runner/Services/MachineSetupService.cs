using System.Diagnostics;
using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

/// <inheritdoc />
public sealed class MachineSetupService : IMachineSetupService
{
    private readonly ILogger<MachineSetupService> _logger;

    public MachineSetupService(ILogger<MachineSetupService> logger)
    {
        _logger = logger;
    }

    public async Task<MachineCapabilityInstallResult> InstallCapabilityAsync(string capabilityId, string? version = null, CancellationToken cancellationToken = default)
    {
        var request = capabilityId.Trim().ToLowerInvariant();
        var requestedVersion = NormalizeVersion(version);

        return request switch
        {
            "gh" => await InstallGitHubCliAsync(cancellationToken),
            "copilot" => await InstallCopilotCliAsync(cancellationToken),
            "node" => await InstallNodeAsync(requestedVersion, cancellationToken),
            "python" => await InstallPythonAsync(requestedVersion, cancellationToken),
            "dotnet" => await InstallDotNetAsync(requestedVersion, cancellationToken),
            _ => CreateFailureResult(request, capabilityId, requestedVersion, $"Installing '{capabilityId}' is not supported.")
        };
    }

    private Task<MachineCapabilityInstallResult> InstallGitHubCliAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return RunWindowsCommandAsync(
                "gh",
                "GitHub CLI",
                "winget install --id GitHub.cli -e --accept-package-agreements --accept-source-agreements",
                cancellationToken);
        }

        return RunLinuxCommandAsync(
            "gh",
            "GitHub CLI",
            "apt-get update && apt-get install -y gh",
            cancellationToken);
    }

    private Task<MachineCapabilityInstallResult> InstallCopilotCliAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!CommandExists("npm"))
            {
                return Task.FromResult(CreateFailureResult(
                    "copilot",
                    "GitHub Copilot CLI",
                    null,
                    "npm is required to install GitHub Copilot CLI. Install Node.js first."));
            }

            return RunDirectCommandAsync(
                "copilot",
                "GitHub Copilot CLI",
                "npm",
                ["install", "-g", "@github/copilot"],
                cancellationToken);
        }

        return RunLinuxCommandAsync(
            "copilot",
            "GitHub Copilot CLI",
            "if ! command -v curl >/dev/null 2>&1; then apt-get update && apt-get install -y curl; fi && curl -fsSL https://gh.io/copilot-install | bash",
            cancellationToken);
    }

    private Task<MachineCapabilityInstallResult> InstallNodeAsync(string? requestedVersion, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return RunWindowsCommandAsync(
                "node",
                "Node.js",
                BuildWindowsNodeInstallCommand(requestedVersion),
                cancellationToken,
                requestedVersion);
        }

        return RunLinuxCommandAsync(
            "node",
            "Node.js",
            BuildLinuxNodeInstallCommand(requestedVersion),
            cancellationToken,
            requestedVersion);
    }

    private Task<MachineCapabilityInstallResult> InstallPythonAsync(string? requestedVersion, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var version = requestedVersion ?? "3.12";
            return RunWindowsCommandAsync(
                "python",
                "Python",
                $"winget install --id Python.Python.{version} -e --accept-package-agreements --accept-source-agreements",
                cancellationToken,
                version);
        }

        var commandText = requestedVersion is null
            ? "apt-get update && apt-get install -y python3 python3-pip python3-venv pipx"
            : $"apt-get update && apt-get install -y python{requestedVersion} python{requestedVersion}-venv python3-pip";

        return RunLinuxCommandAsync(
            "python",
            "Python",
            commandText,
            cancellationToken,
            requestedVersion);
    }

    private Task<MachineCapabilityInstallResult> InstallDotNetAsync(string? requestedVersion, CancellationToken cancellationToken)
    {
        var version = requestedVersion ?? "10.0";
        if (OperatingSystem.IsWindows())
        {
            var major = version.Split('.', 2)[0];
            var commandText = $"winget install --id Microsoft.DotNet.SDK.{major} -e --accept-package-agreements --accept-source-agreements";
            if (version.Contains('.'))
            {
                commandText += $" --version {QuoteWindowsArgument(version)}";
            }

            return RunWindowsCommandAsync(
                "dotnet",
                ".NET SDK",
                commandText,
                cancellationToken,
                version);
        }

        return RunLinuxCommandAsync(
            "dotnet",
            ".NET SDK",
            $"wget https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb && dpkg -i /tmp/packages-microsoft-prod.deb && rm /tmp/packages-microsoft-prod.deb && apt-get update && apt-get install -y dotnet-sdk-{version}",
            cancellationToken,
            version);
    }

    private Task<MachineCapabilityInstallResult> RunWindowsCommandAsync(
        string capabilityId,
        string capabilityName,
        string commandText,
        CancellationToken cancellationToken,
        string? requestedVersion = null)
    {
        if (!CommandExists("winget"))
        {
            return Task.FromResult(CreateFailureResult(
                capabilityId,
                capabilityName,
                requestedVersion,
                "winget is required for this installation flow but is not available on the machine."));
        }

        return RunDirectCommandAsync(
            capabilityId,
            capabilityName,
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", commandText],
            cancellationToken,
            commandText,
            requestedVersion);
    }

    private Task<MachineCapabilityInstallResult> RunLinuxCommandAsync(
        string capabilityId,
        string capabilityName,
        string commandText,
        CancellationToken cancellationToken,
        string? requestedVersion = null)
    {
        if (!CommandExists("apt-get"))
        {
            return Task.FromResult(CreateFailureResult(
                capabilityId,
                capabilityName,
                requestedVersion,
                "apt-get is required for this installation flow but is not available on the machine."));
        }

        var shellPath = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
        var nonInteractiveCommand = $"export DEBIAN_FRONTEND=noninteractive && {commandText}";
        var finalCommand =
            $"if [ \"$(id -u)\" -eq 0 ]; then sh -lc {QuotePosix(nonInteractiveCommand)}; " +
            "elif command -v sudo >/dev/null 2>&1; then " +
            "if id -un >/dev/null 2>&1 && sudo -n true >/dev/null 2>&1; then " +
            $"sudo -n sh -lc {QuotePosix(nonInteractiveCommand)}; " +
            "elif ! id -un >/dev/null 2>&1; then " +
            "echo 'Linux setup actions cannot use sudo because the current UID is not present in /etc/passwd.' >&2; exit 1; " +
            "else echo 'Linux setup actions require passwordless sudo for the current user.' >&2; exit 1; fi; " +
            "else echo 'Linux setup actions require root or passwordless sudo.' >&2; exit 1; fi";

        return RunDirectCommandAsync(
            capabilityId,
            capabilityName,
            shellPath,
            ["-lc", finalCommand],
            cancellationToken,
            finalCommand,
            requestedVersion);
    }

    private async Task<MachineCapabilityInstallResult> RunDirectCommandAsync(
        string capabilityId,
        string capabilityName,
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? commandTextOverride = null,
        string? requestedVersion = null)
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
            return new MachineCapabilityInstallResult
            {
                CapabilityId = capabilityId,
                CapabilityName = capabilityName,
                RequestedVersion = requestedVersion,
                Succeeded = false,
                ExitCode = -1,
                CommandText = commandTextOverride ?? $"{fileName} {string.Join(' ', arguments)}",
                Message = ex.Message,
                StandardError = ex.Message
            };
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;

        if (process.ExitCode != 0)
        {
            _logger.LogWarning(
                "Machine setup for {CapabilityId} failed with exit code {ExitCode}: {Error}",
                capabilityId,
                process.ExitCode,
                standardError);
        }

        return new MachineCapabilityInstallResult
        {
            CapabilityId = capabilityId,
            CapabilityName = capabilityName,
            RequestedVersion = requestedVersion,
            Succeeded = process.ExitCode == 0,
            ExitCode = process.ExitCode,
            CommandText = commandTextOverride ?? $"{fileName} {string.Join(' ', arguments)}",
            StandardOutput = standardOutput,
            StandardError = standardError,
            Message = process.ExitCode == 0
                ? $"Installed {capabilityName}{(requestedVersion is null ? string.Empty : $" {requestedVersion}")}."
                : FirstMeaningfulLine(standardError, standardOutput)
        };
    }

    private static string BuildWindowsNodeInstallCommand(string? requestedVersion)
    {
        var baseCommand = requestedVersion switch
        {
            null => "winget install --id OpenJS.NodeJS.LTS -e --accept-package-agreements --accept-source-agreements",
            var version when !version.Contains('.') && version.StartsWith("20", StringComparison.OrdinalIgnoreCase)
                => "winget install --id OpenJS.NodeJS.LTS -e --accept-package-agreements --accept-source-agreements",
            var version when !version.Contains('.')
                => "winget install --id OpenJS.NodeJS -e --accept-package-agreements --accept-source-agreements",
            _ => "winget install --id OpenJS.NodeJS -e --accept-package-agreements --accept-source-agreements"
        };

        return requestedVersion is not null && requestedVersion.Contains('.')
            ? $"{baseCommand} --version {QuoteWindowsArgument(requestedVersion)}"
            : baseCommand;
    }

    private static string BuildLinuxNodeInstallCommand(string? requestedVersion)
    {
        var major = ExtractLeadingMajor(requestedVersion) ?? "22";
        return $"if ! command -v curl >/dev/null 2>&1; then apt-get update && apt-get install -y curl ca-certificates; fi && curl -fsSL https://deb.nodesource.com/setup_{major}.x | bash - && apt-get install -y nodejs";
    }

    private static string? ExtractLeadingMajor(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var digits = new string(version.Trim().TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 ? digits : null;
    }

    private static MachineCapabilityInstallResult CreateFailureResult(
        string capabilityId,
        string capabilityName,
        string? requestedVersion,
        string message)
    {
        return new MachineCapabilityInstallResult
        {
            CapabilityId = capabilityId,
            CapabilityName = capabilityName,
            RequestedVersion = requestedVersion,
            Succeeded = false,
            ExitCode = -1,
            Message = message,
            StandardError = message
        };
    }

    private static bool CommandExists(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string[] candidates = OperatingSystem.IsWindows()
            ? [command, $"{command}.exe", $"{command}.cmd", $"{command}.bat"]
            : [command];

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidate in candidates)
            {
                if (File.Exists(Path.Combine(directory, candidate)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        return version.Trim();
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

    private static string QuotePosix(string value) => $"'{value.Replace("'", "'\"'\"'")}'";
    private static string QuoteWindowsArgument(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
