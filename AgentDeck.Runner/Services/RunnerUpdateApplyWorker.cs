using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

internal static class RunnerUpdateApplyWorker
{
    public const string HelperModeSwitch = "--runner-update-apply-worker";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string planPath, CancellationToken cancellationToken = default)
    {
        RunnerUpdateApplyPlan? plan = null;
        try
        {
            plan = JsonSerializer.Deserialize<RunnerUpdateApplyPlan>(
                await File.ReadAllTextAsync(planPath, cancellationToken),
                JsonOptions)
                ?? throw new InvalidOperationException($"Runner update apply plan '{planPath}' was empty.");

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
            return 1;
        }
        finally
        {
            TryDeleteFile(planPath);
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

        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }

        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(file, destinationPath, overwrite: true);
        }
    }

    private static void CopyLocalConfiguration(string sourceInstallDirectory, string candidateInstallDirectory)
    {
        foreach (var file in Directory.GetFiles(sourceInstallDirectory, "appsettings*.json", SearchOption.TopDirectoryOnly))
        {
            File.Copy(file, Path.Combine(candidateInstallDirectory, Path.GetFileName(file)), overwrite: true);
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
