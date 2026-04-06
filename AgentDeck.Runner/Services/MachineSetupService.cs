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

    public async Task<MachineCapabilityInstallResult> InstallCapabilityAsync(string capabilityId, CancellationToken cancellationToken = default)
    {
        var request = capabilityId.Trim().ToLowerInvariant();
        return request switch
        {
            "gh" => await InstallGitHubCliAsync(cancellationToken),
            "copilot" => await InstallCopilotCliAsync(cancellationToken),
            "node" => await InstallNodeAsync(cancellationToken),
            "python" => await InstallPythonAsync(cancellationToken),
            "dotnet" => await InstallDotNetAsync(cancellationToken),
            _ => new MachineCapabilityInstallResult
            {
                CapabilityId = request,
                CapabilityName = capabilityId,
                Succeeded = false,
                ExitCode = -1,
                Message = $"Installing '{capabilityId}' is not supported."
            }
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
        if (!CommandExists("npm"))
        {
            return Task.FromResult(new MachineCapabilityInstallResult
            {
                CapabilityId = "copilot",
                CapabilityName = "GitHub Copilot CLI",
                Succeeded = false,
                ExitCode = -1,
                Message = "npm is required to install GitHub Copilot CLI. Install Node.js first."
            });
        }

        return RunDirectCommandAsync(
            "copilot",
            "GitHub Copilot CLI",
            "npm",
            ["install", "-g", "@github/copilot"],
            cancellationToken);
    }

    private Task<MachineCapabilityInstallResult> InstallNodeAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return RunWindowsCommandAsync(
                "node",
                "Node.js",
                "winget install --id OpenJS.NodeJS.LTS -e --accept-package-agreements --accept-source-agreements",
                cancellationToken);
        }

        return RunLinuxCommandAsync(
            "node",
            "Node.js",
            "apt-get update && apt-get install -y nodejs npm",
            cancellationToken);
    }

    private Task<MachineCapabilityInstallResult> InstallPythonAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return RunWindowsCommandAsync(
                "python",
                "Python",
                "winget install --id Python.Python.3.12 -e --accept-package-agreements --accept-source-agreements",
                cancellationToken);
        }

        return RunLinuxCommandAsync(
            "python",
            "Python",
            "apt-get update && apt-get install -y python3 python3-pip python3-venv pipx",
            cancellationToken);
    }

    private Task<MachineCapabilityInstallResult> InstallDotNetAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return RunWindowsCommandAsync(
                "dotnet",
                ".NET SDK",
                "winget install --id Microsoft.DotNet.SDK.10 -e --accept-package-agreements --accept-source-agreements",
                cancellationToken);
        }

        return RunLinuxCommandAsync(
            "dotnet",
            ".NET SDK",
            "wget https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -O packages-microsoft-prod.deb && dpkg -i packages-microsoft-prod.deb && rm packages-microsoft-prod.deb && apt-get update && apt-get install -y dotnet-sdk-10.0",
            cancellationToken);
    }

    private Task<MachineCapabilityInstallResult> RunWindowsCommandAsync(
        string capabilityId,
        string capabilityName,
        string commandText,
        CancellationToken cancellationToken)
    {
        if (!CommandExists("winget"))
        {
            return Task.FromResult(new MachineCapabilityInstallResult
            {
                CapabilityId = capabilityId,
                CapabilityName = capabilityName,
                Succeeded = false,
                ExitCode = -1,
                Message = "winget is required for this installation flow but is not available on the machine."
            });
        }

        return RunDirectCommandAsync(
            capabilityId,
            capabilityName,
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", commandText],
            cancellationToken,
            commandText);
    }

    private Task<MachineCapabilityInstallResult> RunLinuxCommandAsync(
        string capabilityId,
        string capabilityName,
        string commandText,
        CancellationToken cancellationToken)
    {
        if (!CommandExists("apt-get"))
        {
            return Task.FromResult(new MachineCapabilityInstallResult
            {
                CapabilityId = capabilityId,
                CapabilityName = capabilityName,
                Succeeded = false,
                ExitCode = -1,
                Message = "apt-get is required for this installation flow but is not available on the machine."
            });
        }

        var shellPath = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
        var finalCommand = $"if command -v sudo >/dev/null 2>&1; then sudo sh -lc {QuotePosix(commandText)}; else sh -lc {QuotePosix(commandText)}; fi";

        return RunDirectCommandAsync(
            capabilityId,
            capabilityName,
            shellPath,
            ["-lc", finalCommand],
            cancellationToken,
            finalCommand);
    }

    private async Task<MachineCapabilityInstallResult> RunDirectCommandAsync(
        string capabilityId,
        string capabilityName,
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? commandTextOverride = null)
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
            Succeeded = process.ExitCode == 0,
            ExitCode = process.ExitCode,
            CommandText = commandTextOverride ?? $"{fileName} {string.Join(' ', arguments)}",
            StandardOutput = standardOutput,
            StandardError = standardError,
            Message = process.ExitCode == 0 ? $"Installed {capabilityName}." : FirstMeaningfulLine(standardError, standardOutput)
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
}
