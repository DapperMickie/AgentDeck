using System.Diagnostics;
using AgentDeck.Core.Models;
using AgentDeck.Core.Services;

namespace AgentDeck.Services;

/// <inheritdoc />
public sealed class LocalDockerWorkloadRuntimeService : IWorkloadContainerRuntimeService
{
    private readonly IAppDataDirectory _appDataDirectory;
    private readonly IWorkloadContainerCommandService _commandService;

    public LocalDockerWorkloadRuntimeService(IAppDataDirectory appDataDirectory, IWorkloadContainerCommandService commandService)
    {
        _appDataDirectory = appDataDirectory;
        _commandService = commandService;
    }

    public async Task<WorkloadContainerStatus> GetStatusAsync(RunnerMachineSettings machine, WorkloadDefinition workload, CancellationToken cancellationToken = default)
    {
        var commandSet = _commandService.Resolve(machine, workload);
        var dockerVersion = await RunDockerAsync(["version", "--format", "{{.Server.Version}}"], cancellationToken);
        if (!dockerVersion.Succeeded)
        {
            return new WorkloadContainerStatus
            {
                DockerAvailable = false,
                ErrorMessage = FirstMeaningfulLine(dockerVersion.StandardError, dockerVersion.StandardOutput)
            };
        }

        var baseImageExists = await RunDockerAsync(["image", "inspect", commandSet.BaseImageTag, "--format", "{{.Id}}"], cancellationToken);
        var workloadImageExists = await RunDockerAsync(["image", "inspect", commandSet.WorkloadImageTag, "--format", "{{.Id}}"], cancellationToken);
        var containerState = await RunDockerAsync(["container", "inspect", commandSet.ContainerName, "--format", "{{.State.Status}}"], cancellationToken);
        var containerId = await RunDockerAsync(["container", "inspect", commandSet.ContainerName, "--format", "{{.Id}}"], cancellationToken);
        var authVolumeExists = await GetVolumeStateAsync(workload, workload.AuthMounts, cancellationToken);
        var cacheVolumeExists = await GetVolumeStateAsync(workload, workload.CacheMounts, cancellationToken);
        var secretAvailability = workload.Secrets.ToDictionary(
            secret => secret.EnvironmentVariable,
            secret => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(secret.EnvironmentVariable)),
            StringComparer.OrdinalIgnoreCase);

        return new WorkloadContainerStatus
        {
            DockerAvailable = true,
            DockerVersion = dockerVersion.StandardOutput.Trim(),
            BaseImageExists = baseImageExists.Succeeded,
            WorkloadImageExists = workloadImageExists.Succeeded,
            ContainerExists = containerState.Succeeded,
            ContainerState = containerState.Succeeded ? containerState.StandardOutput.Trim() : "not-created",
            ContainerId = containerId.Succeeded ? containerId.StandardOutput.Trim() : string.Empty,
            AuthVolumeExists = authVolumeExists,
            CacheVolumeExists = cacheVolumeExists,
            SecretAvailability = secretAvailability
        };
    }

    public async Task<WorkloadContainerExecutionResult> BuildBaseImageAsync(RunnerMachineSettings machine, WorkloadDefinition workload, CancellationToken cancellationToken = default)
    {
        var commandSet = _commandService.Resolve(machine, workload);
        if (string.IsNullOrWhiteSpace(commandSet.BuildBaseImageCommand))
        {
            throw new InvalidOperationException("Set the runner source path before building the base image.");
        }

        return await RunDockerAsync(
            ["build", "-t", commandSet.BaseImageTag, "-f", Path.Combine(machine.RunnerSourcePath, "AgentDeck.Runner", "Dockerfile"), machine.RunnerSourcePath],
            cancellationToken);
    }

    public async Task<WorkloadContainerExecutionResult> BuildWorkloadImageAsync(RunnerMachineSettings machine, WorkloadDefinition workload, CancellationToken cancellationToken = default)
    {
        var commandSet = _commandService.Resolve(machine, workload);
        var buildDirectory = PrepareGeneratedBuildContext(commandSet);

        return await RunDockerAsync(
            ["build", "-t", commandSet.WorkloadImageTag, "-f", "Dockerfile.generated", "."],
            cancellationToken,
            buildDirectory);
    }

    public async Task<WorkloadContainerExecutionResult> StartContainerAsync(RunnerMachineSettings machine, WorkloadDefinition workload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(machine.DockerWorkspacePath))
            throw new InvalidOperationException("Set the host workspace path before starting the container.");

        var commandSet = _commandService.Resolve(machine, workload);
        await RunDockerAsync(["rm", "-f", commandSet.ContainerName], cancellationToken);

        var args = new List<string>
        {
            "run", "-d", "--restart", "unless-stopped",
            "--name", commandSet.ContainerName
        };

        var runnerUrl = new Uri(machine.RunnerUrl);
        args.AddRange(["-p", $"{runnerUrl.Port}:{runnerUrl.Port}"]);
        args.AddRange(["-e", $"AGENTDECK_PORT={runnerUrl.Port}"]);

        foreach (var pair in workload.EnvironmentVariables.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            args.AddRange(["-e", $"{pair.Key}={pair.Value}"]);

        foreach (var secret in workload.Secrets.OrderBy(secret => secret.EnvironmentVariable, StringComparer.OrdinalIgnoreCase))
        {
            var secretValue = Environment.GetEnvironmentVariable(secret.EnvironmentVariable);
            if (string.IsNullOrWhiteSpace(secretValue))
            {
                if (secret.IsRequired)
                {
                    throw new InvalidOperationException(
                        $"The required environment variable '{secret.EnvironmentVariable}' is not set on the host.");
                }

                continue;
            }

            args.AddRange(["-e", $"{secret.EnvironmentVariable}={secretValue}"]);
        }

        args.AddRange(["-v", $"{machine.DockerWorkspacePath}:{workload.WorkspaceMountPath}"]);

        foreach (var mount in workload.AuthMounts)
            args.AddRange(["-v", $"{GetNamedVolumeName(workload, mount.Name)}:{mount.TargetPath}"]);

        foreach (var mount in workload.CacheMounts)
            args.AddRange(["-v", $"{GetNamedVolumeName(workload, mount.Name)}:{mount.TargetPath}"]);

        args.Add(commandSet.WorkloadImageTag);
        return await RunDockerAsync(args, cancellationToken);
    }

    public Task<WorkloadContainerExecutionResult> StopContainerAsync(RunnerMachineSettings machine, WorkloadDefinition workload, CancellationToken cancellationToken = default)
    {
        var commandSet = _commandService.Resolve(machine, workload);
        return RunDockerAsync(["rm", "-f", commandSet.ContainerName], cancellationToken);
    }

    private string PrepareGeneratedBuildContext(WorkloadContainerCommandSet commandSet)
    {
        var workdir = Path.Combine(_appDataDirectory.Path, "docker", commandSet.ContainerName);
        Directory.CreateDirectory(workdir);
        File.WriteAllText(Path.Combine(workdir, "Dockerfile.generated"), commandSet.GeneratedDockerfile);
        return workdir;
    }

    private static string GetNamedVolumeName(WorkloadDefinition workload, string mountName)
    {
        return $"agentdeck-{workload.Id.ToLowerInvariant()}-{mountName.Trim().ToLowerInvariant().Replace(' ', '-')}";
    }

    private static async Task<IReadOnlyDictionary<string, bool>> GetVolumeStateAsync(
        WorkloadDefinition workload,
        IEnumerable<WorkloadMount> mounts,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var mount in mounts)
        {
            var volumeName = GetNamedVolumeName(workload, mount.Name);
            var inspect = await RunDockerAsync(["volume", "inspect", volumeName], cancellationToken);
            result[mount.Name] = inspect.Succeeded;
        }

        return result;
    }

    private static string FirstMeaningfulLine(string primary, string secondary)
    {
        foreach (var candidate in new[] { primary, secondary })
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            var line = candidate.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(line))
                return line;
        }

        return "Docker is unavailable.";
    }

    private static async Task<WorkloadContainerExecutionResult> RunDockerAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
            startInfo.WorkingDirectory = workingDirectory;

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new WorkloadContainerExecutionResult
            {
                Succeeded = false,
                ExitCode = -1,
                CommandText = $"docker {string.Join(' ', arguments)}",
                StandardError = ex.Message
            };
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new WorkloadContainerExecutionResult
        {
            Succeeded = process.ExitCode == 0,
            ExitCode = process.ExitCode,
            CommandText = $"docker {string.Join(' ', arguments)}",
            StandardOutput = await stdOutTask,
            StandardError = await stdErrTask
        };
    }
}
