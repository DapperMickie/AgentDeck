using System.Diagnostics;
using System.Text.RegularExpressions;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

/// <inheritdoc />
public sealed class MachineSetupService : IMachineSetupService
{
    private readonly IRunnerSetupCatalogService _setupCatalog;
    private readonly ILogger<MachineSetupService> _logger;

    public MachineSetupService(
        IRunnerSetupCatalogService setupCatalog,
        ILogger<MachineSetupService> logger)
    {
        _setupCatalog = setupCatalog;
        _logger = logger;
    }

    public Task<MachineCapabilityInstallResult> InstallCapabilityAsync(string capabilityId, string? version = null, CancellationToken cancellationToken = default) =>
        ExecuteCapabilityActionAsync(capabilityId, "install", version, cancellationToken);

    public Task<MachineCapabilityInstallResult> UpdateCapabilityAsync(string capabilityId, CancellationToken cancellationToken = default) =>
        ExecuteCapabilityActionAsync(capabilityId, "update", cancellationToken: cancellationToken);

    private async Task<MachineCapabilityInstallResult> ExecuteCapabilityActionAsync(
        string capabilityId,
        string action,
        string? requestedVersion = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedCapabilityId = NormalizeValue(capabilityId);
        if (normalizedCapabilityId is null)
        {
            return CreateFailureResult("unknown", capabilityId, action, requestedVersion, "Capability id is required.");
        }

        var catalog = await _setupCatalog.GetCurrentCatalogAsync(cancellationToken);
        if (catalog is null)
        {
            return CreateFailureResult(
                normalizedCapabilityId,
                capabilityId,
                action,
                requestedVersion,
                "Runner has not reconciled a setup catalog from the coordinator yet.");
        }

        var capability = catalog.Capabilities.FirstOrDefault(candidate =>
            string.Equals(candidate.CapabilityId, normalizedCapabilityId, StringComparison.OrdinalIgnoreCase));
        if (capability is null)
        {
            return CreateFailureResult(
                normalizedCapabilityId,
                capabilityId,
                action,
                requestedVersion,
                $"Setup catalog does not define capability '{capabilityId}'.");
        }

        var actionDefinition = capability.Actions.FirstOrDefault(candidate =>
            string.Equals(candidate.Action, action, StringComparison.OrdinalIgnoreCase));
        if (actionDefinition is null)
        {
            return CreateFailureResult(
                normalizedCapabilityId,
                capability.DisplayName,
                action,
                requestedVersion,
                $"Setup catalog does not define an '{action}' action for '{capability.DisplayName}'.");
        }

        var platform = GetCurrentPlatform();
        var normalizedRequestedVersion = NormalizeValue(requestedVersion);
        var recipe = actionDefinition.Recipes.FirstOrDefault(candidate => RecipeMatches(candidate, platform, normalizedRequestedVersion));
        if (recipe is null)
        {
            return CreateFailureResult(
                normalizedCapabilityId,
                capability.DisplayName,
                action,
                normalizedRequestedVersion,
                $"Setup catalog does not define a matching {platform} recipe for '{capability.DisplayName}' {action}.");
        }

        var effectiveVersion = normalizedRequestedVersion ?? NormalizeValue(recipe.DefaultVersion);
        var missingCommand = recipe.RequiredCommands
            .Select(RenderTemplate)
            .FirstOrDefault(command => !string.IsNullOrWhiteSpace(command) && !CommandExists(command));
        if (missingCommand is not null)
        {
            return CreateFailureResult(
                normalizedCapabilityId,
                capability.DisplayName,
                action,
                effectiveVersion,
                BuildMissingCommandMessage(recipe, missingCommand, action));
        }

        return recipe.ExecutionKind switch
        {
            RunnerSetupRecipeExecutionKind.PlatformShell => await ExecutePlatformShellRecipeAsync(
                normalizedCapabilityId,
                capability.DisplayName,
                action,
                recipe,
                effectiveVersion,
                cancellationToken),
            RunnerSetupRecipeExecutionKind.DirectCommand => await ExecuteDirectRecipeAsync(
                normalizedCapabilityId,
                capability.DisplayName,
                action,
                recipe,
                effectiveVersion,
                cancellationToken),
            _ => CreateFailureResult(
                normalizedCapabilityId,
                capability.DisplayName,
                action,
                effectiveVersion,
                $"Unsupported setup recipe execution kind '{recipe.ExecutionKind}'.")
        };

        string RenderTemplate(string value) => ApplyTemplate(value, effectiveVersion);
    }

    private async Task<MachineCapabilityInstallResult> ExecutePlatformShellRecipeAsync(
        string capabilityId,
        string capabilityName,
        string action,
        RunnerSetupRecipe recipe,
        string? effectiveVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(recipe.CommandText))
        {
            return CreateFailureResult(capabilityId, capabilityName, action, effectiveVersion, "Setup recipe requires command text.");
        }

        var commandText = ApplyTemplate(recipe.CommandText, effectiveVersion);
        return GetCurrentPlatform() switch
        {
            RunnerHostPlatform.Windows => await RunWindowsShellCommandAsync(capabilityId, capabilityName, commandText, cancellationToken, action, effectiveVersion),
            RunnerHostPlatform.Linux => await RunLinuxShellCommandAsync(capabilityId, capabilityName, commandText, cancellationToken, action, effectiveVersion),
            _ => CreateFailureResult(capabilityId, capabilityName, action, effectiveVersion, "Platform shell setup recipes are not supported on this runner platform.")
        };
    }

    private async Task<MachineCapabilityInstallResult> ExecuteDirectRecipeAsync(
        string capabilityId,
        string capabilityName,
        string action,
        RunnerSetupRecipe recipe,
        string? effectiveVersion,
        CancellationToken cancellationToken)
    {
        var fileName = NormalizeValue(recipe.FileName);
        if (fileName is null)
        {
            return CreateFailureResult(capabilityId, capabilityName, action, effectiveVersion, "Direct-command setup recipe requires a file name.");
        }

        var arguments = recipe.Arguments
            .Select(argument => ApplyTemplate(argument, effectiveVersion))
            .ToArray();
        return await RunDirectCommandAsync(
            capabilityId,
            capabilityName,
            fileName,
            arguments,
            cancellationToken,
            action,
            null,
            effectiveVersion);
    }

    private Task<MachineCapabilityInstallResult> RunWindowsShellCommandAsync(
        string capabilityId,
        string capabilityName,
        string commandText,
        CancellationToken cancellationToken,
        string action,
        string? requestedVersion = null) =>
        RunDirectCommandAsync(
            capabilityId,
            capabilityName,
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", commandText],
            cancellationToken,
            action,
            commandText,
            requestedVersion);

    private Task<MachineCapabilityInstallResult> RunLinuxShellCommandAsync(
        string capabilityId,
        string capabilityName,
        string commandText,
        CancellationToken cancellationToken,
        string action,
        string? requestedVersion = null)
    {
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
            action,
            finalCommand,
            requestedVersion);
    }

    private async Task<MachineCapabilityInstallResult> RunDirectCommandAsync(
        string capabilityId,
        string capabilityName,
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string action,
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
                Action = action,
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
            Action = action,
            RequestedVersion = requestedVersion,
            Succeeded = process.ExitCode == 0,
            ExitCode = process.ExitCode,
            CommandText = commandTextOverride ?? $"{fileName} {string.Join(' ', arguments)}",
            StandardOutput = standardOutput,
            StandardError = standardError,
            Message = process.ExitCode == 0
                ? $"{(action.Equals("update", StringComparison.OrdinalIgnoreCase) ? "Updated" : "Installed")} {capabilityName}{(requestedVersion is null ? string.Empty : $" {requestedVersion}")}."
                : FirstMeaningfulLine(standardError, standardOutput)
        };
    }

    private static bool RecipeMatches(RunnerSetupRecipe recipe, RunnerHostPlatform platform, string? requestedVersion)
    {
        if (recipe.Platform != platform)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(requestedVersion))
        {
            return recipe.MatchWhenVersionMissing || string.IsNullOrWhiteSpace(recipe.MatchVersionPattern);
        }

        if (string.IsNullOrWhiteSpace(recipe.MatchVersionPattern))
        {
            return !recipe.MatchWhenVersionMissing;
        }

        return Regex.IsMatch(requestedVersion, recipe.MatchVersionPattern, RegexOptions.IgnoreCase);
    }

    private static string ApplyTemplate(string value, string? requestedVersion)
    {
        var normalizedVersion = NormalizeValue(requestedVersion);
        var major = ExtractLeadingMajor(normalizedVersion) ?? string.Empty;
        return value
            .Replace("{requestedVersion}", normalizedVersion ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{major}", major, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildMissingCommandMessage(RunnerSetupRecipe recipe, string missingCommand, string action) =>
        NormalizeValue(recipe.RequiredCommandsMessage) is { } configured
            ? configured.Replace("{command}", missingCommand, StringComparison.OrdinalIgnoreCase)
            : $"{missingCommand} is required for this {action} flow but is not available on the machine.";

    private static RunnerHostPlatform GetCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return RunnerHostPlatform.Windows;
        }

        if (OperatingSystem.IsLinux())
        {
            return RunnerHostPlatform.Linux;
        }

        if (OperatingSystem.IsMacOS())
        {
            return RunnerHostPlatform.MacOS;
        }

        return RunnerHostPlatform.Unknown;
    }

    private static MachineCapabilityInstallResult CreateFailureResult(
        string capabilityId,
        string capabilityName,
        string action,
        string? requestedVersion,
        string message)
    {
        return new MachineCapabilityInstallResult
        {
            CapabilityId = capabilityId,
            CapabilityName = capabilityName,
            Action = action,
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

    private static string? ExtractLeadingMajor(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var digits = new string(version.Trim().TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 ? digits : null;
    }

    private static string? NormalizeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
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
