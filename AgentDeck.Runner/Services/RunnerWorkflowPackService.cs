using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using AgentDeck.Runner.Configuration;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;
using Microsoft.Extensions.Options;

namespace AgentDeck.Runner.Services;

public sealed class RunnerWorkflowPackService : IRunnerWorkflowPackService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly WorkerCoordinatorOptions _options;
    private readonly IMachineCapabilityService _capabilities;
    private readonly IMachineSetupService _machineSetup;
    private readonly ILogger<RunnerWorkflowPackService> _logger;
    private RunnerWorkflowPackStatus? _currentStatus;

    public RunnerWorkflowPackService(
        IOptions<WorkerCoordinatorOptions> options,
        IMachineCapabilityService capabilities,
        IMachineSetupService machineSetup,
        ILogger<RunnerWorkflowPackService> logger)
    {
        _options = options.Value;
        _capabilities = capabilities;
        _machineSetup = machineSetup;
        _logger = logger;
        _currentStatus = LoadPersistedStatus();
    }

    public async Task<RunnerWorkflowPackStatus?> GetCurrentStatusAsync(CancellationToken cancellationToken = default)
    {
        await _reconcileGate.WaitAsync(cancellationToken);
        try
        {
            return GetCurrentStatusSnapshot();
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    public async Task<RunnerWorkflowPackStatus?> ReconcileDesiredWorkflowPackAsync(
        HttpClient coordinatorClient,
        RunnerDesiredState desiredState,
        CancellationToken cancellationToken = default)
    {
        await _reconcileGate.WaitAsync(cancellationToken);
        try
        {
            var currentStatus = GetCurrentStatusSnapshot();
            if (desiredState.DesiredWorkflowPack is null)
            {
                return await UpdateStatusAsync(null, cancellationToken);
            }

            var desiredPackId = desiredState.DesiredWorkflowPack.DefinitionId;
            var desiredPackVersion = desiredState.DesiredWorkflowPack.Version;
            if (!desiredState.SecurityPolicy.AllowWorkflowPackExecution)
            {
                return await UpdateStatusAsync(new RunnerWorkflowPackStatus
                {
                    State = RunnerWorkflowPackState.Blocked,
                    PackId = desiredPackId,
                    PackVersion = desiredPackVersion,
                    LocalPackPath = GetRetainedLocalPackPath(currentStatus, desiredPackId, desiredPackVersion),
                    FetchedAt = GetRetainedFetchedAt(currentStatus, desiredPackId, desiredPackVersion),
                    StatusMessage = $"Coordinator security policy {desiredState.SecurityPolicy.PolicyVersion} does not permit workflow pack execution."
                }, cancellationToken);
            }

            if (currentStatus?.State == RunnerWorkflowPackState.Ready &&
                string.Equals(currentStatus.PackId, desiredPackId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(currentStatus.PackVersion, desiredPackVersion, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(currentStatus.LocalPackPath) &&
                File.Exists(currentStatus.LocalPackPath))
            {
                return currentStatus;
            }

            try
            {
                var pack = await coordinatorClient.GetFromJsonAsync<RunnerWorkflowPack>(
                    $"api/runner-definitions/workflow-packs/{Uri.EscapeDataString(desiredPackId)}",
                    cancellationToken);

                if (pack is null)
                {
                    return await UpdateStatusAsync(new RunnerWorkflowPackStatus
                    {
                        State = RunnerWorkflowPackState.Failed,
                        PackId = desiredPackId,
                        PackVersion = desiredPackVersion,
                        LocalPackPath = GetRetainedLocalPackPath(currentStatus, desiredPackId, desiredPackVersion),
                        FetchedAt = GetRetainedFetchedAt(currentStatus, desiredPackId, desiredPackVersion),
                        FailureMessage = "Coordinator returned an empty workflow pack response."
                    }, cancellationToken);
                }

                if (!string.Equals(pack.PackId, desiredPackId, StringComparison.OrdinalIgnoreCase))
                {
                    return await UpdateStatusAsync(new RunnerWorkflowPackStatus
                    {
                        State = RunnerWorkflowPackState.Failed,
                        PackId = desiredPackId,
                        PackVersion = desiredPackVersion,
                        LocalPackPath = GetRetainedLocalPackPath(currentStatus, desiredPackId, desiredPackVersion),
                        FetchedAt = GetRetainedFetchedAt(currentStatus, desiredPackId, desiredPackVersion),
                        FailureMessage = $"Coordinator workflow pack id '{pack.PackId}' did not match desired pack '{desiredPackId}'."
                    }, cancellationToken);
                }

                if (!string.Equals(pack.Version, desiredPackVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return await UpdateStatusAsync(new RunnerWorkflowPackStatus
                    {
                        State = RunnerWorkflowPackState.Failed,
                        PackId = desiredPackId,
                        PackVersion = desiredPackVersion,
                        LocalPackPath = GetRetainedLocalPackPath(currentStatus, desiredPackId, desiredPackVersion),
                        FetchedAt = GetRetainedFetchedAt(currentStatus, desiredPackId, desiredPackVersion),
                        FailureMessage = $"Coordinator workflow pack version '{pack.Version}' did not match desired version '{desiredPackVersion}'."
                    }, cancellationToken);
                }

                var packDirectory = Path.Combine(GetWorkflowPackRoot(), SanitizePathComponent(pack.PackId), SanitizePathComponent(pack.Version));
                Directory.CreateDirectory(packDirectory);
                var packPath = Path.Combine(packDirectory, "workflow-pack.json");
                await WriteTextAtomicallyAsync(packPath, JsonSerializer.Serialize(pack, JsonOptions), cancellationToken);
                var fetchedAt = DateTimeOffset.UtcNow;
                var execution = await ExecuteWorkflowPackAsync(pack, cancellationToken);

                return await UpdateStatusAsync(new RunnerWorkflowPackStatus
                {
                    State = execution.Succeeded ? RunnerWorkflowPackState.Ready : RunnerWorkflowPackState.Failed,
                    PackId = pack.PackId,
                    PackVersion = pack.Version,
                    LocalPackPath = packPath,
                    FetchedAt = fetchedAt,
                    StatusMessage = execution.Succeeded ? execution.Message : null,
                    FailureMessage = execution.Succeeded ? null : execution.Message
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                _logger.LogWarning(ex, "Failed to reconcile desired workflow pack {PackId}@{PackVersion}", desiredPackId, desiredPackVersion);
                return await UpdateStatusAsync(new RunnerWorkflowPackStatus
                {
                    State = RunnerWorkflowPackState.Failed,
                    PackId = desiredPackId,
                    PackVersion = desiredPackVersion,
                    LocalPackPath = GetRetainedLocalPackPath(currentStatus, desiredPackId, desiredPackVersion),
                    FetchedAt = GetRetainedFetchedAt(currentStatus, desiredPackId, desiredPackVersion),
                    FailureMessage = ex.Message
                }, CancellationToken.None);
            }
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    public void Dispose() => _reconcileGate.Dispose();

    private async Task<RunnerWorkflowPackStatus?> UpdateStatusAsync(RunnerWorkflowPackStatus? status, CancellationToken cancellationToken)
    {
        var statusPath = GetStatusPath();
        Directory.CreateDirectory(Path.GetDirectoryName(statusPath)!);
        RunnerWorkflowPackStatus? previousStatus;
        lock (_lock)
        {
            previousStatus = _currentStatus;
        }

        if (status is null)
        {
            TryDeleteFile(statusPath);
            CleanupOldWorkflowPackFiles(previousStatus, null);
            lock (_lock)
            {
                _currentStatus = null;
            }

            return null;
        }

        await WriteTextAtomicallyAsync(statusPath, JsonSerializer.Serialize(status, JsonOptions), cancellationToken);
        CleanupOldWorkflowPackFiles(previousStatus, status);
        lock (_lock)
        {
            _currentStatus = status;
        }

        return status;
    }

    private RunnerWorkflowPackStatus? LoadPersistedStatus()
    {
        var statusPath = GetStatusPath();
        if (!File.Exists(statusPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(statusPath);
            return JsonSerializer.Deserialize<RunnerWorkflowPackStatus>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted workflow pack status from {StatusPath}", statusPath);
            return null;
        }
    }

    private RunnerWorkflowPackStatus? GetCurrentStatusSnapshot()
    {
        lock (_lock)
        {
            return _currentStatus;
        }
    }

    private string GetStatusPath() => Path.Combine(GetWorkflowPackRoot(), "current-workflow-pack-status.json");

    private string GetWorkflowPackRoot()
    {
        if (!string.IsNullOrWhiteSpace(_options.WorkflowPackRoot))
        {
            return _options.WorkflowPackRoot.Trim();
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentDeck",
            "workflow-packs");
    }

    private void CleanupOldWorkflowPackFiles(RunnerWorkflowPackStatus? previousStatus, RunnerWorkflowPackStatus? currentStatus)
    {
        var previousPath = previousStatus?.LocalPackPath;
        if (string.IsNullOrWhiteSpace(previousPath))
        {
            return;
        }

        if (string.Equals(previousPath, currentStatus?.LocalPackPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var previousDirectory = Path.GetDirectoryName(previousPath);
        if (string.IsNullOrWhiteSpace(previousDirectory) || !Directory.Exists(previousDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(previousDirectory, recursive: true);

            var packDirectory = Path.GetDirectoryName(previousDirectory);
            if (!string.IsNullOrWhiteSpace(packDirectory) &&
                Directory.Exists(packDirectory) &&
                !Directory.EnumerateFileSystemEntries(packDirectory).Any())
            {
                Directory.Delete(packDirectory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up stale workflow pack files under {WorkflowPackDirectory}", previousDirectory);
        }
    }

    private static string? GetRetainedLocalPackPath(RunnerWorkflowPackStatus? currentStatus, string desiredPackId, string desiredPackVersion) =>
        IsSamePack(currentStatus, desiredPackId, desiredPackVersion) &&
        !string.IsNullOrWhiteSpace(currentStatus?.LocalPackPath) &&
        File.Exists(currentStatus.LocalPackPath)
            ? currentStatus.LocalPackPath
            : null;

    private static DateTimeOffset? GetRetainedFetchedAt(RunnerWorkflowPackStatus? currentStatus, string desiredPackId, string desiredPackVersion) =>
        IsSamePack(currentStatus, desiredPackId, desiredPackVersion)
            ? currentStatus?.FetchedAt
            : null;

    private static bool IsSamePack(RunnerWorkflowPackStatus? currentStatus, string desiredPackId, string desiredPackVersion) =>
        currentStatus is not null &&
        string.Equals(currentStatus.PackId, desiredPackId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(currentStatus.PackVersion, desiredPackVersion, StringComparison.OrdinalIgnoreCase);

    private static async Task WriteTextAtomicallyAsync(string path, string contents, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException($"Path '{path}' has no parent directory.");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(tempPath, contents, cancellationToken);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private async Task<WorkflowPackExecutionResult> ExecuteWorkflowPackAsync(RunnerWorkflowPack pack, CancellationToken cancellationToken)
    {
        if (pack.Steps.Count == 0)
        {
            return WorkflowPackExecutionResult.Success($"Workflow pack {pack.PackId}@{pack.Version} contains no steps to execute.");
        }

        var messages = new List<string>(pack.Steps.Count);
        foreach (var step in pack.Steps)
        {
            var result = await ExecuteStepAsync(step, cancellationToken);
            if (!result.Succeeded)
            {
                return WorkflowPackExecutionResult.Failure($"{GetStepDisplayName(step)} failed. {result.Message}");
            }

            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                messages.Add($"{GetStepDisplayName(step)}: {result.Message}");
            }
        }

        var summary = $"Workflow pack {pack.PackId}@{pack.Version} executed {pack.Steps.Count} step(s) successfully.";
        if (messages.Count == 0)
        {
            return WorkflowPackExecutionResult.Success(summary);
        }

        return WorkflowPackExecutionResult.Success($"{summary} {string.Join("; ", messages)}");
    }

    private async Task<WorkflowPackExecutionResult> ExecuteStepAsync(RunnerWorkflowStep step, CancellationToken cancellationToken)
    {
        return step.Kind switch
        {
            RunnerWorkflowStepKind.VerifyInstalledTool => await VerifyInstalledToolAsync(step, cancellationToken),
            RunnerWorkflowStepKind.RunCommand => await ExecuteNamedCommandAsync(step, cancellationToken),
            _ => WorkflowPackExecutionResult.Failure(
                $"Workflow step kind '{step.Kind}' is not supported by this runner yet.")
        };
    }

    private async Task<WorkflowPackExecutionResult> VerifyInstalledToolAsync(RunnerWorkflowStep step, CancellationToken cancellationToken)
    {
        var toolId = GetInput(step, "tool");
        var probeCommand = GetInput(step, "command");
        var failIfMissing = GetBooleanInput(step, "failIfMissing");
        if (string.IsNullOrWhiteSpace(toolId) && string.IsNullOrWhiteSpace(probeCommand))
        {
            return WorkflowPackExecutionResult.Failure(
                "VerifyInstalledTool requires either a 'tool' input or a 'command' input.");
        }

        if (!string.IsNullOrWhiteSpace(toolId))
        {
            var snapshot = await _capabilities.GetSnapshotAsync(cancellationToken);
            var capability = snapshot.Capabilities.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, toolId, StringComparison.OrdinalIgnoreCase));
            if (capability?.Status == MachineCapabilityStatus.Installed)
            {
                var versionText = string.IsNullOrWhiteSpace(capability.Version) ? string.Empty : $" {capability.Version}";
                return WorkflowPackExecutionResult.Success($"{capability.Name} detected{versionText}.");
            }

            if (capability is not null && string.IsNullOrWhiteSpace(probeCommand))
            {
                var message = string.IsNullOrWhiteSpace(capability.Message)
                    ? $"{capability.Name} is not installed."
                    : $"{capability.Name} is not installed. {capability.Message}";
                return failIfMissing
                    ? WorkflowPackExecutionResult.Failure(message)
                    : WorkflowPackExecutionResult.Success(message);
            }
        }

        if (string.IsNullOrWhiteSpace(probeCommand))
        {
            var missingMessage = $"Tool '{toolId}' is not installed.";
            return failIfMissing
                ? WorkflowPackExecutionResult.Failure(missingMessage)
                : WorkflowPackExecutionResult.Success(missingMessage);
        }

        var probeResult = await RunShellCommandAsync(probeCommand, cancellationToken);
        if (probeResult.Succeeded)
        {
            return WorkflowPackExecutionResult.Success($"Probe command '{probeCommand}' succeeded.");
        }

        var failureMessage = FirstMeaningfulLine(probeResult.StandardError, probeResult.StandardOutput)
            ?? $"Probe command '{probeCommand}' exited with code {probeResult.ExitCode}.";
        return failIfMissing
            ? WorkflowPackExecutionResult.Failure(failureMessage)
            : WorkflowPackExecutionResult.Success($"Probe command '{probeCommand}' did not detect the tool yet.");
    }

    private async Task<WorkflowPackExecutionResult> ExecuteNamedCommandAsync(RunnerWorkflowStep step, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(step.CommandText))
        {
            return WorkflowPackExecutionResult.Failure("RunCommand requires a command identifier.");
        }

        MachineCapabilityInstallResult result;
        switch (NormalizeCommandIdentifier(step.CommandText))
        {
            case "install-gh":
            case "install-github-cli":
                result = await _machineSetup.InstallCapabilityAsync("gh", cancellationToken: cancellationToken);
                break;
            case "install-copilot":
            case "install-copilot-cli":
                result = await _machineSetup.InstallCapabilityAsync("copilot", cancellationToken: cancellationToken);
                break;
            case "install-node":
            case "install-nodejs":
                result = await _machineSetup.InstallCapabilityAsync("node", GetInput(step, "version"), cancellationToken);
                break;
            case "install-python":
                result = await _machineSetup.InstallCapabilityAsync("python", GetInput(step, "version"), cancellationToken);
                break;
            case "install-dotnet":
            case "install-dotnet-sdk":
                result = await _machineSetup.InstallCapabilityAsync("dotnet", GetInput(step, "version"), cancellationToken);
                break;
            case "update-gh":
            case "update-github-cli":
                result = await _machineSetup.UpdateCapabilityAsync("gh", cancellationToken);
                break;
            case "update-copilot":
            case "update-copilot-cli":
                result = await _machineSetup.UpdateCapabilityAsync("copilot", cancellationToken);
                break;
            default:
                return WorkflowPackExecutionResult.Failure(
                    $"Workflow command '{step.CommandText}' is not supported by this runner.");
        }

        if (result.Succeeded)
        {
            return WorkflowPackExecutionResult.Success(result.Message ?? $"{result.CapabilityName} {result.Action} succeeded.");
        }

        var failureMessage = FirstMeaningfulLine(result.StandardError, result.StandardOutput)
            ?? result.Message
            ?? $"{result.CapabilityName} {result.Action} failed with exit code {result.ExitCode}.";
        return WorkflowPackExecutionResult.Failure(failureMessage);
    }

    private static async Task<ShellCommandResult> RunShellCommandAsync(string commandText, CancellationToken cancellationToken)
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell.exe")
            : new ProcessStartInfo("/bin/sh");

        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(commandText);
        }
        else
        {
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(commandText);
        }

        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new ShellCommandResult(false, -1, string.Empty, ex.Message);
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new ShellCommandResult(
            process.ExitCode == 0,
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask);
    }

    private static string? GetInput(RunnerWorkflowStep step, string key) =>
        step.Inputs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static bool GetBooleanInput(RunnerWorkflowStep step, string key) =>
        step.Inputs.TryGetValue(key, out var value) &&
        bool.TryParse(value, out var parsed) &&
        parsed;

    private static string NormalizeCommandIdentifier(string commandText) => commandText.Trim().ToLowerInvariant();

    private static string GetStepDisplayName(RunnerWorkflowStep step) =>
        string.IsNullOrWhiteSpace(step.DisplayName) ? step.StepId : step.DisplayName.Trim();

    private static string? FirstMeaningfulLine(params string?[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var line = value
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line;
            }
        }

        return null;
    }

    private static string SanitizePathComponent(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new char[value.Length];
        var index = 0;
        foreach (var character in value)
        {
            builder[index++] = invalidChars.Contains(character) ? '_' : character;
        }

        return new string(builder, 0, index);
    }

    private readonly record struct WorkflowPackExecutionResult(bool Succeeded, string Message)
    {
        public static WorkflowPackExecutionResult Success(string message) => new(true, message);

        public static WorkflowPackExecutionResult Failure(string message) => new(false, message);
    }

    private readonly record struct ShellCommandResult(bool Succeeded, int ExitCode, string StandardOutput, string StandardError);
}
