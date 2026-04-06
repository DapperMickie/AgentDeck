using System.Text;
using AgentDeck.Core.Models;

namespace AgentDeck.Core.Services;

/// <inheritdoc />
public sealed class WorkloadContainerCommandService : IWorkloadContainerCommandService
{
    public WorkloadContainerCommandSet Resolve(RunnerMachineSettings machine, WorkloadDefinition workload)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(workload);

        var normalizedWorkloadId = workload.Id.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedWorkloadId))
            throw new InvalidOperationException("A workload must have a valid id before container commands can be generated.");

        var baseImageTag = workload.BaseImage.Trim();
        if (string.IsNullOrWhiteSpace(baseImageTag))
            throw new InvalidOperationException("The workload base image is required.");

        var workloadImageTag = $"agentdeck-workload-{normalizedWorkloadId}";
        var normalizedMachineId = machine.Id.Trim().ToLowerInvariant();
        var containerName = $"agentdeck-runner-{normalizedMachineId}-{normalizedWorkloadId}";
        var buildBaseImageCommand = string.IsNullOrWhiteSpace(machine.RunnerSourcePath)
            ? null
            : $"docker build -t {Quote(baseImageTag)} -f {Quote(Path.Combine(machine.RunnerSourcePath, "AgentDeck.Runner", "Dockerfile"))} {Quote(machine.RunnerSourcePath)}";

        var generatedDockerfile = GenerateDockerfile(workload);

        return new WorkloadContainerCommandSet
        {
            BaseImageTag = baseImageTag,
            WorkloadImageTag = workloadImageTag,
            ContainerName = containerName,
            BuildBaseImageCommand = buildBaseImageCommand,
            GeneratedDockerfile = generatedDockerfile,
            BuildWorkloadImageCommand = $"docker build -t {Quote(workloadImageTag)} -f Dockerfile.generated .",
            StartContainerCommand = GenerateStartCommand(machine, workload, containerName, workloadImageTag),
            StopContainerCommand = $"docker rm -f {Quote(containerName)}",
        };
    }

    private static string GenerateDockerfile(WorkloadDefinition workload)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"FROM {workload.BaseImage}");
        builder.AppendLine("USER root");
        builder.AppendLine("ENV DEBIAN_FRONTEND=noninteractive");

        var aptPackages = new HashSet<string>(workload.CliInstallers.AptPackages, StringComparer.OrdinalIgnoreCase);
        var installNode = workload.SdkVersions.ContainsKey("node");
        var installPython = workload.SdkVersions.ContainsKey("python");
        var installDotNet = workload.SdkVersions.ContainsKey("dotnet");

        if (installNode || installPython || installDotNet || aptPackages.Count > 0)
        {
            builder.AppendLine("RUN apt-get update && apt-get install -y \\");
            builder.AppendLine("    ca-certificates \\");
            builder.AppendLine("    curl \\");
            builder.AppendLine("    gnupg \\");
            builder.AppendLine("    wget \\");
            builder.AppendLine("    && rm -rf /var/lib/apt/lists/*");
        }

        if (installNode)
        {
            var nodeMajor = workload.SdkVersions["node"].Split('.')[0];
            builder.AppendLine("RUN mkdir -p /etc/apt/keyrings \\");
            builder.AppendLine("    && curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg \\");
            builder.AppendLine($"    && echo \"deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_{nodeMajor}.x nodistro main\" > /etc/apt/sources.list.d/nodesource.list \\");
            builder.AppendLine("    && apt-get update \\");
            builder.AppendLine("    && apt-get install -y nodejs \\");
            builder.AppendLine("    && rm -rf /var/lib/apt/lists/*");
        }

        if (installPython)
        {
            builder.AppendLine("RUN apt-get update \\");
            builder.AppendLine("    && apt-get install -y python3 python3-pip python3-venv pipx \\");
            builder.AppendLine("    && rm -rf /var/lib/apt/lists/*");
        }

        if (installDotNet)
        {
            var dotnetMajorMinor = workload.SdkVersions["dotnet"];
            builder.AppendLine("RUN wget https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -O packages-microsoft-prod.deb \\");
            builder.AppendLine("    && dpkg -i packages-microsoft-prod.deb \\");
            builder.AppendLine("    && rm packages-microsoft-prod.deb \\");
            builder.AppendLine("    && apt-get update \\");
            builder.AppendLine($"    && apt-get install -y dotnet-sdk-{dotnetMajorMinor} \\");
            builder.AppendLine("    && rm -rf /var/lib/apt/lists/*");
        }

        if (aptPackages.Count > 0)
        {
            builder.AppendLine("RUN apt-get update \\");
            builder.AppendLine($"    && apt-get install -y {string.Join(' ', aptPackages.OrderBy(package => package, StringComparer.OrdinalIgnoreCase))} \\");
            builder.AppendLine("    && rm -rf /var/lib/apt/lists/*");
        }

        if (workload.CliInstallers.NpmGlobalPackages.Count > 0)
            builder.AppendLine($"RUN npm install -g {string.Join(' ', workload.CliInstallers.NpmGlobalPackages)}");

        if (workload.CliInstallers.PipxPackages.Count > 0)
            builder.AppendLine($"RUN pipx install {string.Join(' ', workload.CliInstallers.PipxPackages)}");

        if (workload.CliInstallers.DotNetTools.Count > 0)
        {
            foreach (var tool in workload.CliInstallers.DotNetTools)
                builder.AppendLine($"RUN dotnet tool install --tool-path /usr/local/bin {tool}");
        }

        foreach (var pair in workload.EnvironmentVariables.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            builder.AppendLine($"ENV {pair.Key}={QuoteDockerValue(pair.Value)}");

        if (workload.BootstrapCommands.Count > 0)
            builder.AppendLine($"RUN {string.Join(" && ", workload.BootstrapCommands)}");

        return builder.ToString().TrimEnd();
    }

    private static string GenerateStartCommand(RunnerMachineSettings machine, WorkloadDefinition workload, string containerName, string workloadImageTag)
    {
        if (string.IsNullOrWhiteSpace(machine.DockerWorkspacePath))
            throw new InvalidOperationException("A host workspace path is required before generating the start command.");

        var runnerUrl = new Uri(machine.RunnerUrl);
        var port = runnerUrl.Port;
        var parts = new List<string>
        {
            "docker run -d --restart unless-stopped",
            $"--name {Quote(containerName)}",
            $"-p {port}:{port}",
            $"-e AGENTDECK_PORT={port}"
        };

        foreach (var pair in workload.EnvironmentVariables.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            parts.Add($"-e {pair.Key}={Quote(pair.Value)}");

        parts.Add($"-v {Quote(machine.DockerWorkspacePath)}:{Quote(workload.WorkspaceMountPath)}");

        foreach (var mount in workload.AuthMounts)
            parts.Add($"-v {Quote(GetNamedVolumeName(workload, mount.Name))}:{Quote(mount.TargetPath)}");

        foreach (var mount in workload.CacheMounts)
            parts.Add($"-v {Quote(GetNamedVolumeName(workload, mount.Name))}:{Quote(mount.TargetPath)}");

        parts.Add(Quote(workloadImageTag));
        return string.Join(" ", parts);
    }

    private static string GetNamedVolumeName(WorkloadDefinition workload, string mountName)
    {
        return $"agentdeck-{workload.Id.ToLowerInvariant()}-{mountName.Trim().ToLowerInvariant().Replace(' ', '-')}";
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
    private static string QuoteDockerValue(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
