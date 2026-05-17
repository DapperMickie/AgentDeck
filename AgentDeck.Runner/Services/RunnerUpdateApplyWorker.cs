using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Json;
using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

internal static class RunnerUpdateApplyWorker
{
    public const string HelperModeSwitch = "--runner-update-apply-worker";
    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.WebIndented;

    public static async Task<int> RunAsync(string planPath, CancellationToken cancellationToken = default)
    {
        RunnerUpdateApplyPlan? plan = null;
        try
        {
            plan = JsonSerializer.Deserialize<RunnerUpdateApplyPlan>(
                await File.ReadAllTextAsync(planPath, cancellationToken),
                JsonOptions)
                ?? throw new InvalidOperationException($"Runner update apply plan '{planPath}' was empty.");

            await VerifyApplyWorkerIntegrityAsync(plan, cancellationToken);
            await WaitForTargetExitAsync(plan.TargetProcessId, plan.ProcessExitTimeout, cancellationToken);

            if (!File.Exists(plan.ArtifactPath))
            {
                throw new InvalidOperationException($"Staged update artifact '{plan.ArtifactPath}' does not exist.");
            }

            if (!string.Equals(Path.GetExtension(plan.ArtifactPath), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Runner update apply currently requires a staged .zip payload.");
            }

            PrepareCandidateInstall(plan);
            await WriteStatusAsync(new RunnerUpdateStatus
            {
                State = RunnerUpdateStageState.Applied,
                ManifestId = plan.ManifestId,
                ManifestVersion = plan.ManifestVersion,
                StagingDirectory = plan.StagingDirectory,
                StagedArtifactPath = plan.ArtifactPath,
                AppliedInstallDirectory = plan.CandidateInstallDirectory,
                StagedAt = plan.ApplyStartedAt,
                ApplyStartedAt = plan.ApplyStartedAt,
                AppliedAt = DateTimeOffset.UtcNow
            }, plan.StatusPath, cancellationToken);

            StartCandidateRunner(plan.CandidateInstallDirectory);
            return 0;
        }
        catch (Exception ex)
        {
            if (plan is not null)
            {
                await WriteStatusAsync(new RunnerUpdateStatus
                {
                    State = RunnerUpdateStageState.Failed,
                    ManifestId = plan.ManifestId,
                    ManifestVersion = plan.ManifestVersion,
                    StagingDirectory = plan.StagingDirectory,
                    StagedArtifactPath = plan.ArtifactPath,
                    AppliedInstallDirectory = plan.CandidateInstallDirectory,
                    StagedAt = plan.ApplyStartedAt,
                    ApplyStartedAt = plan.ApplyStartedAt,
                    FailureMessage = ex.Message
                }, plan.StatusPath, cancellationToken);
            }

            Console.Error.WriteLine(ex);
            TryWriteEarlyFailureLog(planPath, plan, ex);
            return 1;
        }
        finally
        {
            TryDeleteFile(planPath);
        }
    }

    private static async Task VerifyApplyWorkerIntegrityAsync(RunnerUpdateApplyPlan plan, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plan.ApplyWorkerPath) || string.IsNullOrWhiteSpace(plan.ApplyWorkerSha256))
        {
            throw new InvalidOperationException("Runner update apply plan did not include apply-worker integrity metadata.");
        }

        var currentWorkerPath = Environment.ProcessPath;
        if (string.Equals(Path.GetFileNameWithoutExtension(currentWorkerPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            currentWorkerPath = typeof(RunnerUpdateApplyWorker).Assembly.Location;
        }

        if (!string.Equals(Path.GetFullPath(currentWorkerPath ?? string.Empty), Path.GetFullPath(plan.ApplyWorkerPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Runner update apply helper path did not match the planned helper path.");
        }

        var actualSha256 = await ComputeSha256Async(plan.ApplyWorkerPath, cancellationToken);
        if (!string.Equals(actualSha256, plan.ApplyWorkerSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Runner update apply helper SHA-256 did not match the planned helper hash.");
        }
    }

    private static async Task WaitForTargetExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        while (true)
        {
            if (!IsProcessStillRunning(processId))
            {
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!IsProcessStillRunning(processId))
                {
                    return;
                }

                throw new TimeoutException($"Runner update apply timed out waiting for process {processId} to exit.");
            }
        }
    }

    private static void PrepareCandidateInstall(RunnerUpdateApplyPlan plan)
    {
        var tempExtractionDirectory = Path.Combine(plan.StagingDirectory, $"apply-extract-{Guid.NewGuid():N}");
        try
        {
            if (Directory.Exists(plan.CandidateInstallDirectory))
            {
                Directory.Delete(plan.CandidateInstallDirectory, recursive: true);
            }

            Directory.CreateDirectory(tempExtractionDirectory);
            ZipFile.ExtractToDirectory(plan.ArtifactPath, tempExtractionDirectory, overwriteFiles: true);

            var archiveRoot = ResolveArchiveRoot(tempExtractionDirectory);
            CopyDirectoryContents(archiveRoot, plan.CandidateInstallDirectory);
            CopyLocalConfiguration(plan.SourceInstallDirectory, plan.CandidateInstallDirectory);
        }
        finally
        {
            if (Directory.Exists(tempExtractionDirectory))
            {
                Directory.Delete(tempExtractionDirectory, recursive: true);
            }
        }
    }

    private static string ResolveArchiveRoot(string extractionDirectory)
    {
        var directories = Directory.GetDirectories(extractionDirectory);
        var files = Directory.GetFiles(extractionDirectory);
        return directories.Length == 1 && files.Length == 0
            ? directories[0]
            : extractionDirectory;
    }

    private static void CopyDirectoryContents(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        var destinationRoot = Path.GetFullPath(destinationDirectory);

        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            var targetDirectory = Path.Combine(destinationRoot, relativePath);
            EnsureUnderRoot(destinationRoot, targetDirectory);
            Directory.CreateDirectory(targetDirectory);
        }

        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destinationPath = Path.Combine(destinationRoot, relativePath);
            EnsureUnderRoot(destinationRoot, destinationPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(file, destinationPath, overwrite: true);
        }
    }

    private static void EnsureUnderRoot(string root, string candidatePath)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var normalizedCandidate = Path.GetFullPath(candidatePath);
        var rootWithSep = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!normalizedCandidate.Equals(normalizedRoot, comparison) &&
            !normalizedCandidate.StartsWith(rootWithSep, comparison))
        {
            throw new InvalidOperationException(
                $"Refusing to write '{normalizedCandidate}' which is outside the candidate install directory '{normalizedRoot}'.");
        }
    }

    private static void CopyLocalConfiguration(string sourceInstallDirectory, string candidateInstallDirectory)
    {
        var sourceUserSettingsPath = Path.Combine(sourceInstallDirectory, "appsettings.user.json");
        var candidateUserSettingsPath = Path.Combine(candidateInstallDirectory, "appsettings.user.json");
        if (File.Exists(sourceUserSettingsPath))
        {
            File.Copy(sourceUserSettingsPath, candidateUserSettingsPath, overwrite: true);
            return;
        }

        var legacySettingsPath = Path.Combine(sourceInstallDirectory, "appsettings.json");
        if (!File.Exists(legacySettingsPath))
        {
            return;
        }

        var migratedUserSettings = BuildLegacyUserSettingsOverlay(legacySettingsPath);
        if (migratedUserSettings is null)
        {
            return;
        }

        File.WriteAllText(candidateUserSettingsPath, migratedUserSettings);
    }

    private static string? BuildLegacyUserSettingsOverlay(string legacySettingsPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(legacySettingsPath));
        var root = document.RootElement;
        var overlay = new Dictionary<string, object?>();

        CopySectionProperties(root, overlay, "Runner", ["WorkspaceRoot", "Port", "AllowedOrigins"]);
        CopySectionProperties(root, overlay, "Coordinator",
        [
            "MachineId",
            "MachineName",
            "CoordinatorUrl",
            "ControlChannelTransport",
            "AdvertisedRunnerUrl",
            "AllowInsecureHttpCoordinatorForLoopback",
            "AllowInsecureHttpCoordinatorForDevelopment",
            "DownloadUpdatePayload",
            "UpdateApplyProcessExitTimeout"
        ]);

        return overlay.Count == 0
            ? null
            : JsonSerializer.Serialize(overlay, JsonOptions);
    }

    private static void CopySectionProperties(JsonElement root, Dictionary<string, object?> overlay, string sectionName, IReadOnlyCollection<string> propertyNames)
    {
        if (!root.TryGetProperty(sectionName, out var section) || section.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var sectionOverlay = new Dictionary<string, object?>();
        foreach (var property in section.EnumerateObject())
        {
            if (propertyNames.Contains(property.Name))
            {
                sectionOverlay[property.Name] = property.Value.Deserialize<object?>(JsonOptions);
            }
        }

        if (sectionOverlay.Count > 0)
        {
            overlay[sectionName] = sectionOverlay;
        }
    }

    private static void StartCandidateRunner(string candidateInstallDirectory)
    {
        var executablePath = OperatingSystem.IsWindows()
            ? Path.Combine(candidateInstallDirectory, "AgentDeck.Runner.exe")
            : Path.Combine(candidateInstallDirectory, "AgentDeck.Runner");
        if (File.Exists(executablePath))
        {
            var executableStart = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = candidateInstallDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var executableProcess = Process.Start(executableStart)
                ?? throw new InvalidOperationException($"Failed to start updated runner from '{executablePath}'.");
            return;
        }

        var runnerDllPath = Path.Combine(candidateInstallDirectory, "AgentDeck.Runner.dll");
        if (!File.Exists(runnerDllPath))
        {
            throw new InvalidOperationException($"Updated runner install in '{candidateInstallDirectory}' did not contain AgentDeck.Runner.dll.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = candidateInstallDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(runnerDllPath);
        using var runnerProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start updated runner from '{runnerDllPath}'.");
    }

    private static bool IsProcessStillRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task WriteStatusAsync(RunnerUpdateStatus status, string statusPath, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(statusPath) ?? throw new InvalidOperationException($"Status path '{statusPath}' has no parent directory.");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $"{Path.GetFileName(statusPath)}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(status, JsonOptions), cancellationToken);
        File.Move(tempPath, statusPath, overwrite: true);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryWriteEarlyFailureLog(string planPath, RunnerUpdateApplyPlan? plan, Exception exception)
    {
        // The update-apply worker is a child process — its stderr is not persisted, so a crash
        // before WriteStatusAsync has nothing observable. Drop a sibling .log next to whichever
        // path we have (preferred: staging dir, fallback: directory of the plan file) so the
        // parent runner can surface it on next start.
        try
        {
            var stagingDir = plan?.StagingDirectory;
            var logDirectory = !string.IsNullOrWhiteSpace(stagingDir) && Directory.Exists(stagingDir)
                ? stagingDir
                : Path.GetDirectoryName(planPath);

            if (string.IsNullOrWhiteSpace(logDirectory))
            {
                return;
            }

            Directory.CreateDirectory(logDirectory);
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
            var logPath = Path.Combine(logDirectory, $"runner-update-apply.{timestamp}.log");
            File.WriteAllText(
                logPath,
                $"[{DateTimeOffset.UtcNow:O}] runner-update-apply-worker failed.{Environment.NewLine}" +
                $"planPath: {planPath}{Environment.NewLine}" +
                $"manifestId: {plan?.ManifestId}{Environment.NewLine}" +
                $"manifestVersion: {plan?.ManifestVersion}{Environment.NewLine}" +
                $"stagingDirectory: {plan?.StagingDirectory}{Environment.NewLine}" +
                $"candidateInstallDirectory: {plan?.CandidateInstallDirectory}{Environment.NewLine}" +
                $"{exception}{Environment.NewLine}");
        }
        catch
        {
            // Best-effort. We're already in the failure handler — never throw from a logger.
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
}
