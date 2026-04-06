using System.Diagnostics;
using System.Text.RegularExpressions;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

/// <inheritdoc />
public sealed partial class MachineCapabilityService : IMachineCapabilityService
{
    private readonly ILogger<MachineCapabilityService> _logger;

    private static readonly CapabilityProbe[] Probes =
    [
        new("gh", "GitHub CLI", "cli", [new ProbeCommand("gh", ["--version"])], ParseFirstLineVersion),
        new("copilot", "GitHub Copilot CLI", "cli", [new ProbeCommand("copilot", ["--version"])], ParseFirstLineVersion),
        new("node", "Node.js", "sdk", [new ProbeCommand("node", ["--version"])], ParseTrimmedOutput),
        new("python", "Python", "sdk", [new ProbeCommand("python", ["--version"]), new ProbeCommand("python3", ["--version"])], ParseTrimmedOutput),
        new("dotnet", ".NET SDK", "sdk", [new ProbeCommand("dotnet", ["--version"])], ParseTrimmedOutput)
    ];

    public MachineCapabilityService(ILogger<MachineCapabilityService> logger)
    {
        _logger = logger;
    }

    public async Task<MachineCapabilitiesSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var capabilities = new List<MachineCapability>(Probes.Length);

        foreach (var probe in Probes)
        {
            capabilities.Add(await DetectAsync(probe, cancellationToken));
        }

        return new MachineCapabilitiesSnapshot
        {
            CapturedAt = DateTimeOffset.UtcNow,
            Capabilities = capabilities
        };
    }

    private async Task<MachineCapability> DetectAsync(CapabilityProbe probe, CancellationToken cancellationToken)
    {
        string? lastError = null;

        foreach (var command in probe.Commands)
        {
            var result = await RunCommandAsync(command.FileName, command.Arguments, cancellationToken);
            if (result.StartFailed)
            {
                lastError = result.ErrorMessage;
                continue;
            }

            if (!result.Succeeded)
            {
                return new MachineCapability
                {
                    Id = probe.Id,
                    Name = probe.Name,
                    Category = probe.Category,
                    Status = MachineCapabilityStatus.Error,
                    Message = FirstMeaningfulLine(result.StandardError, result.StandardOutput) ?? "Detection failed."
                };
            }

            return new MachineCapability
            {
                Id = probe.Id,
                Name = probe.Name,
                Category = probe.Category,
                Status = MachineCapabilityStatus.Installed,
                Version = probe.VersionParser(result.StandardOutput, result.StandardError),
                Message = $"Detected via {command.FileName}"
            };
        }

        return new MachineCapability
        {
            Id = probe.Id,
            Name = probe.Name,
            Category = probe.Category,
            Status = MachineCapabilityStatus.Missing,
            Message = lastError ?? "Not installed."
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

    private static string? ParseFirstLineVersion(string standardOutput, string standardError)
    {
        var line = FirstMeaningfulLine(standardOutput, standardError);
        return line;
    }

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

    private sealed record CapabilityProbe(
        string Id,
        string Name,
        string Category,
        IReadOnlyList<ProbeCommand> Commands,
        Func<string, string, string?> VersionParser);

    private sealed record ProbeCommand(string FileName, IReadOnlyList<string> Arguments);

    private sealed class ProbeResult
    {
        public bool Succeeded { get; init; }
        public bool StartFailed { get; init; }
        public string StandardOutput { get; init; } = string.Empty;
        public string StandardError { get; init; } = string.Empty;
        public string? ErrorMessage { get; init; }
    }
}
