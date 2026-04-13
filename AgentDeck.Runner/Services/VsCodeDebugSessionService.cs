using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

/// <inheritdoc />
public sealed class VsCodeDebugSessionService : IVsCodeDebugSessionService
{
    private const string CSharpExtensionId = "ms-dotnettools.csharp";
    private const string CSharpExtensionDisplayName = "C# Dev Kit / C#";
    private const string VsCodeWindowTitle = "Visual Studio Code";
    private const string AgentDeckBuildTaskName = "AgentDeck Build";

    private sealed record ActiveDebugSession(
        Process Process,
        TaskCompletionSource<int> Completion,
        string ViewerSessionId,
        string? DeviceViewerSessionId,
        string WorkspaceDirectory,
        string StartupProjectPath);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly ConcurrentDictionary<string, ActiveDebugSession> _sessions = new();
    private readonly ConcurrentDictionary<string, VsCodeDebugSession> _sessionRecords = new();
    private readonly IRemoteViewerSessionService _viewers;
    private readonly IDesktopViewerBootstrapService _viewerBootstrap;
    private readonly ILogger<VsCodeDebugSessionService> _logger;

    public VsCodeDebugSessionService(
        IRemoteViewerSessionService viewers,
        IDesktopViewerBootstrapService viewerBootstrap,
        ILogger<VsCodeDebugSessionService> logger)
    {
        _viewers = viewers;
        _viewerBootstrap = viewerBootstrap;
        _logger = logger;
    }

    public IReadOnlyList<VsCodeDebugSession> GetAll() =>
        [.. _sessionRecords.Values.OrderByDescending(session => session.CreatedAt)];

    public VsCodeDebugSession? Get(string orchestrationSessionId) =>
        _sessionRecords.TryGetValue(orchestrationSessionId, out var session) ? session : null;

    public async Task<VsCodeDebugLaunchResult> LaunchAsync(
        string orchestrationSessionId,
        OrchestrationJob job,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var startupProjectPath = ResolveStartupProject(job, workingDirectory);
        var workspaceDirectory = Path.GetDirectoryName(startupProjectPath)
            ?? throw new InvalidOperationException($"Could not determine a workspace directory for '{startupProjectPath}'.");
        var debugConfigurationName = string.IsNullOrWhiteSpace(job.DebugConfigurationName)
            ? $"{job.ProjectName} Debug"
            : job.DebugConfigurationName;

        var viewer = _viewers.Create(new CreateRemoteViewerSessionRequest
        {
            MachineId = job.TargetMachineId,
            MachineName = job.TargetMachineName,
            JobId = job.Id,
            Provider = RemoteViewerProviderKind.Managed,
            Target = new RemoteViewerTarget
            {
                Kind = RemoteViewerTargetKind.VsCode,
                DisplayName = $"{job.ProjectName} VS Code",
                JobId = job.Id,
                SessionId = orchestrationSessionId,
                WindowTitle = BuildWindowTitle(job.ProjectName)
            }
        });

        viewer = await _viewerBootstrap.BootstrapAsync(viewer.Id, cancellationToken: cancellationToken) ?? viewer;

        var debugSessionId = Guid.NewGuid().ToString("N");
        var createdAt = DateTimeOffset.UtcNow;
        _sessionRecords[orchestrationSessionId] = new VsCodeDebugSession
        {
            Id = debugSessionId,
            OrchestrationSessionId = orchestrationSessionId,
            JobId = job.Id,
            ProjectName = job.ProjectName,
            Platform = job.Platform,
            MachineId = job.TargetMachineId,
            MachineName = job.TargetMachineName,
            WorkspaceDirectory = workspaceDirectory,
            StartupProjectPath = startupProjectPath,
            DebugConfigurationName = debugConfigurationName,
            ViewerSessionId = viewer.Id,
            TargetDisplayName = job.DeviceSelection?.DisplayName ?? job.DeviceSelection?.DeviceId ?? job.DeviceSelection?.ProfileId,
            Status = VsCodeDebugSessionStatus.Requested,
            DebuggerVisibility = VsCodeDebuggerVisibilityState.Background,
            StatusMessage = "VS Code debug session requested.",
            RequiredExtensions =
            [
                new VsCodeDebugExtensionRequirement
                {
                    ExtensionId = CSharpExtensionId,
                    DisplayName = CSharpExtensionDisplayName,
                    IsInstalled = false
                }
            ],
            WorkspaceAssets = new VsCodeDebugWorkspaceAssets
            {
                LaunchConfigurationName = debugConfigurationName,
                PreLaunchTaskName = AgentDeckBuildTaskName,
                LaunchSettingsProfileName = debugConfigurationName
            },
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };

        RemoteViewerSession? deviceViewer = null;
        try
        {
            deviceViewer = await TryCreateDeviceViewerAsync(orchestrationSessionId, job, cancellationToken);

            _viewers.Update(viewer.Id, new UpdateRemoteViewerSessionRequest
            {
                Status = RemoteViewerSessionStatus.Preparing,
                Message = "Preparing VS Code debug workspace."
            });
            UpdateSession(orchestrationSessionId, session => WithStatus(
                session,
                VsCodeDebugSessionStatus.PreparingWorkspace,
                "Preparing VS Code debug workspace.",
                VsCodeDebuggerVisibilityState.Background));

            await MaterializeWorkspaceAssetsAsync(job, workspaceDirectory, startupProjectPath, debugConfigurationName, cancellationToken);

            var codeExecutable = ResolveVsCodeExecutable();
            UpdateSession(orchestrationSessionId, session => WithStatus(
                session,
                VsCodeDebugSessionStatus.EnsuringExtensions,
                "Ensuring required VS Code extensions are installed.",
                VsCodeDebuggerVisibilityState.Background));
            await EnsureCSharpExtensionAsync(codeExecutable, cancellationToken);
            UpdateSession(orchestrationSessionId, session => WithExtensions(
                WithStatus(
                    session,
                    VsCodeDebugSessionStatus.LaunchingHost,
                    "Launching VS Code.",
                    VsCodeDebuggerVisibilityState.Background),
                true));

            var process = StartVsCodeProcess(codeExecutable, workspaceDirectory);
            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                completion.TrySetResult(process.ExitCode);
                CloseViewer(viewer.Id, "VS Code session closed.");
                CloseViewer(deviceViewer?.Id, "Device viewer session closed.");
                UpdateSession(orchestrationSessionId, session => WithStatus(
                    session,
                    VsCodeDebugSessionStatus.Closed,
                    "VS Code session closed.",
                    VsCodeDebuggerVisibilityState.Closed));
                process.Dispose();
            };

            if (process.HasExited)
            {
                completion.TrySetResult(process.ExitCode);
                CloseViewer(viewer.Id, "VS Code session closed.");
                CloseViewer(deviceViewer?.Id, "Device viewer session closed.");
                UpdateSession(orchestrationSessionId, session => WithStatus(
                    session,
                    VsCodeDebugSessionStatus.Closed,
                    "VS Code session closed.",
                    VsCodeDebuggerVisibilityState.Closed));
                process.Dispose();
            }

            var activeSession = new ActiveDebugSession(process, completion, viewer.Id, deviceViewer?.Id, workspaceDirectory, startupProjectPath);
            if (!_sessions.TryAdd(orchestrationSessionId, activeSession))
            {
                completion.TrySetResult(-1);
                CloseViewer(viewer.Id, "VS Code session closed.");
                CloseViewer(deviceViewer?.Id, "Device viewer session closed.");
                TryKillProcess(process);
                throw new InvalidOperationException($"A VS Code debug session is already active for orchestration session '{orchestrationSessionId}'.");
            }

            try
            {
                UpdateSession(orchestrationSessionId, session => WithStatus(
                    session,
                    VsCodeDebugSessionStatus.StartingDebugger,
                    "Starting the VS Code debugger.",
                    VsCodeDebuggerVisibilityState.Background));
                await TriggerDebugStartAsync(process, job.ProjectName, cancellationToken);
            }
            catch
            {
                _sessions.TryRemove(orchestrationSessionId, out _);
                TryKillProcess(process);
                throw;
            }

            _viewers.Update(viewer.Id, new UpdateRemoteViewerSessionRequest
            {
                Status = RemoteViewerSessionStatus.Ready,
                Message = $"VS Code is ready for '{debugConfigurationName}'."
            });
            UpdateSession(orchestrationSessionId, session => WithStatus(
                session,
                VsCodeDebugSessionStatus.Running,
                $"VS Code is ready for '{debugConfigurationName}'.",
                VsCodeDebuggerVisibilityState.Visible));

            return new VsCodeDebugLaunchResult
            {
                DebugSessionId = debugSessionId,
                ViewerSessionId = viewer.Id,
                WorkspaceDirectory = workspaceDirectory,
                StartupProjectPath = startupProjectPath
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch VS Code debug session for job {JobId}", job.Id);
            _viewers.Update(viewer.Id, new UpdateRemoteViewerSessionRequest
            {
                Status = RemoteViewerSessionStatus.Failed,
                Message = ex.Message
            });
            CloseViewer(deviceViewer?.Id, "Device viewer session closed because VS Code debugging failed.");
            UpdateSession(orchestrationSessionId, session => WithStatus(
                session,
                VsCodeDebugSessionStatus.Failed,
                ex.Message,
                VsCodeDebuggerVisibilityState.Background));
            throw;
        }
    }

    public async Task<int> WaitForExitAsync(string orchestrationSessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(orchestrationSessionId, out var session))
        {
            throw new InvalidOperationException($"No VS Code debug session is active for orchestration session '{orchestrationSessionId}'.");
        }

        using var registration = cancellationToken.Register(() => session.Completion.TrySetCanceled(cancellationToken));
        try
        {
            return await session.Completion.Task;
        }
        finally
        {
            _sessions.TryRemove(orchestrationSessionId, out _);
        }
    }

    public Task StopAsync(string orchestrationSessionId, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryRemove(orchestrationSessionId, out var session))
        {
            CloseViewer(session.ViewerSessionId, "VS Code session cancelled.");
            CloseViewer(session.DeviceViewerSessionId, "Device viewer session cancelled.");
            session.Completion.TrySetResult(-1);
            TryKillProcess(session.Process);
            UpdateSession(orchestrationSessionId, existing => WithStatus(
                existing,
                VsCodeDebugSessionStatus.Closed,
                "VS Code session cancelled.",
                VsCodeDebuggerVisibilityState.Closed));
        }

        return Task.CompletedTask;
    }

    private static string ResolveStartupProject(OrchestrationJob job, string workingDirectory)
    {
        var projects = Directory
            .EnumerateFiles(workingDirectory, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (projects.Length == 0)
        {
            throw new InvalidOperationException($"No .csproj file was found under '{workingDirectory}'.");
        }

        var preferred = projects.FirstOrDefault(path =>
            string.Equals(Path.GetFileNameWithoutExtension(path), job.ProjectName, StringComparison.OrdinalIgnoreCase));

        if (preferred is not null)
        {
            return preferred;
        }

        if (projects.Length == 1)
        {
            return projects[0];
        }

        throw new InvalidOperationException(
            $"Could not determine a startup project for '{job.ProjectName}' under '{workingDirectory}'. " +
            "Queue the debug job against a workspace that resolves to a single .csproj, or use a project name that matches the startup project file.");
    }

    private static async Task MaterializeWorkspaceAssetsAsync(
        OrchestrationJob job,
        string workspaceDirectory,
        string startupProjectPath,
        string debugConfigurationName,
        CancellationToken cancellationToken)
    {
        var vscodeDirectory = Path.Combine(workspaceDirectory, ".vscode");
        Directory.CreateDirectory(vscodeDirectory);

        await WriteLaunchJsonAsync(Path.Combine(vscodeDirectory, "launch.json"), debugConfigurationName, cancellationToken);
        await WriteTasksJsonAsync(Path.Combine(vscodeDirectory, "tasks.json"), job.BuildCommand, cancellationToken);
        await WriteSettingsJsonAsync(Path.Combine(vscodeDirectory, "settings.json"), cancellationToken);
        await WriteLaunchSettingsAsync(startupProjectPath, job, debugConfigurationName, cancellationToken);
    }

    private static async Task WriteLaunchJsonAsync(string path, string debugConfigurationName, CancellationToken cancellationToken)
    {
        JsonObject root;
        JsonArray configurations;

        if (File.Exists(path))
        {
            root = await ReadJsonObjectAsync(path, cancellationToken);
            configurations = root["configurations"] as JsonArray ?? new JsonArray();
            root["configurations"] = configurations;
        }
        else
        {
            root = new JsonObject
            {
                ["version"] = "0.2.0"
            };
            configurations = new JsonArray();
            root["configurations"] = configurations;
        }

        UpsertNamedObject(configurations, debugConfigurationName, new JsonObject
        {
            ["name"] = debugConfigurationName,
            ["type"] = "dotnet",
            ["request"] = "launch",
            ["launchSettingsProfile"] = debugConfigurationName,
            ["preLaunchTask"] = AgentDeckBuildTaskName
        });

        await WriteJsonAsync(path, root, cancellationToken);
    }

    private static async Task WriteTasksJsonAsync(string path, string buildCommand, CancellationToken cancellationToken)
    {
        JsonObject root;
        JsonArray tasks;

        if (File.Exists(path))
        {
            root = await ReadJsonObjectAsync(path, cancellationToken);
            tasks = root["tasks"] as JsonArray ?? new JsonArray();
            root["tasks"] = tasks;
        }
        else
        {
            root = new JsonObject
            {
                ["version"] = "2.0.0"
            };
            tasks = new JsonArray();
            root["tasks"] = tasks;
        }

        UpsertNamedObject(tasks, AgentDeckBuildTaskName, new JsonObject
        {
            ["label"] = AgentDeckBuildTaskName,
            ["type"] = "shell",
            ["command"] = buildCommand,
            ["problemMatcher"] = "$msCompile",
            ["group"] = new JsonObject
            {
                ["kind"] = "build",
                ["isDefault"] = true
            }
        }, "label");

        await WriteJsonAsync(path, root, cancellationToken);
    }

    private static async Task WriteSettingsJsonAsync(string path, CancellationToken cancellationToken)
    {
        JsonObject root = File.Exists(path)
            ? await ReadJsonObjectAsync(path, cancellationToken)
            : new JsonObject();

        root["csharp.debug.console"] = "integratedTerminal";
        await WriteJsonAsync(path, root, cancellationToken);
    }

    private static async Task WriteLaunchSettingsAsync(
        string startupProjectPath,
        OrchestrationJob job,
        string debugConfigurationName,
        CancellationToken cancellationToken)
    {
        var projectDirectory = Path.GetDirectoryName(startupProjectPath)
            ?? throw new InvalidOperationException($"Could not determine the project directory for '{startupProjectPath}'.");
        var propertiesDirectory = Path.Combine(projectDirectory, "Properties");
        Directory.CreateDirectory(propertiesDirectory);

        var path = Path.Combine(propertiesDirectory, "launchSettings.json");
        JsonObject root = File.Exists(path)
            ? await ReadJsonObjectAsync(path, cancellationToken)
            : new JsonObject();

        var profiles = root["profiles"] as JsonObject ?? new JsonObject();
        root["profiles"] = profiles;

        var profile = new JsonObject
        {
            ["commandName"] = "Project",
            ["workingDirectory"] = projectDirectory
        };

        if (job.Platform is ApplicationTargetPlatform.Linux or ApplicationTargetPlatform.Windows or ApplicationTargetPlatform.MacOS)
        {
            profile["launchBrowser"] = false;
        }

        profiles[debugConfigurationName] = profile;
        await WriteJsonAsync(path, root, cancellationToken);
    }

    private static Process StartVsCodeProcess(string codeExecutable, string workspaceDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = codeExecutable,
            WorkingDirectory = workspaceDirectory,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("--new-window");
        startInfo.ArgumentList.Add("--wait");
        startInfo.ArgumentList.Add(workspaceDirectory);

        try
        {
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException("VS Code did not start a process.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to start VS Code from '{codeExecutable}'. {ex.Message}", ex);
        }
    }

    private static async Task TriggerDebugStartAsync(Process process, string projectName, CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(45));

        var processExitedTask = process.WaitForExitAsync(linkedCts.Token);
        var automationTask = OperatingSystem.IsWindows()
            ? TriggerDebugStartWindowsAsync(projectName, linkedCts.Token)
            : OperatingSystem.IsMacOS()
                ? TriggerDebugStartMacAsync(linkedCts.Token)
                : TriggerDebugStartLinuxAsync(linkedCts.Token);

        var completed = await Task.WhenAny(automationTask, processExitedTask);
        if (completed == processExitedTask)
        {
            throw new InvalidOperationException("VS Code exited before the debug session could be started.");
        }

        await automationTask;
    }

    private static async Task TriggerDebugStartWindowsAsync(string projectName, CancellationToken cancellationToken)
    {
        var title = BuildWindowTitle(projectName);
        var script = string.Join(Environment.NewLine,
        [
            "$shell = New-Object -ComObject WScript.Shell",
            $"$titles = @({QuoteForPowerShell(title)}, {QuoteForPowerShell(VsCodeWindowTitle)})",
            "for ($i = 0; $i -lt 45; $i++) {",
            "    foreach ($candidate in $titles) {",
            "        if ($shell.AppActivate($candidate)) {",
            "            Start-Sleep -Milliseconds 750",
            "            $shell.SendKeys('{F5}')",
            "            exit 0",
            "        }",
            "    }",
            string.Empty,
            "    Start-Sleep -Seconds 1",
            "}",
            string.Empty,
            "throw 'Could not focus the VS Code window to start debugging.'"
        ]);

        await RunHelperProcessAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], cancellationToken);
    }

    private static async Task TriggerDebugStartLinuxAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            throw new InvalidOperationException("VS Code debug automation on Linux requires DISPLAY to be set.");
        }

        if (ResolveExecutableOnPath("xdotool") is null)
        {
            throw new InvalidOperationException("VS Code debug automation on Linux requires xdotool to be installed.");
        }

        var script = """
for i in $(seq 1 45); do
  if xdotool search --name "Visual Studio Code" windowactivate --sync key F5; then
    exit 0
  fi
  sleep 1
done

echo "Could not focus the VS Code window to start debugging." >&2
exit 1
""";

        await RunHelperProcessAsync("/bin/bash", ["-lc", script], cancellationToken);
    }

    private static async Task TriggerDebugStartMacAsync(CancellationToken cancellationToken)
    {
        var script = """
repeat 45 times
    tell application "System Events"
        if exists process "Visual Studio Code" then
            tell application "Visual Studio Code" to activate
            key code 96
            return
        end if
    end tell

    delay 1
end repeat

error "Could not focus the VS Code window to start debugging."
""";

        await RunHelperProcessAsync("osascript", ["-e", script], cancellationToken);
    }

    private static async Task RunHelperProcessAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start helper process '{fileName}'.");

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode == 0)
        {
            return;
        }

        var output = (await standardOutput).Trim();
        var error = (await standardError).Trim();
        var message = string.Join(" ", new[] { output, error }.Where(value => !string.IsNullOrWhiteSpace(value)));
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
            ? $"Helper process '{fileName}' failed with exit code {process.ExitCode}."
            : message);
    }

    private static async Task EnsureCSharpExtensionAsync(string codeExecutable, CancellationToken cancellationToken)
    {
        if (await HasExtensionAsync(codeExecutable, CSharpExtensionId, cancellationToken))
        {
            return;
        }

        await RunHelperProcessAsync(codeExecutable, ["--install-extension", CSharpExtensionId, "--force"], cancellationToken);
    }

    private static async Task<bool> HasExtensionAsync(string codeExecutable, string extensionId, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = codeExecutable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--list-extensions");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to inspect extensions using '{codeExecutable}'.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Failed to inspect VS Code extensions. {error.Trim()}");
        }

        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(installed => string.Equals(installed, extensionId, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveVsCodeExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var candidate in EnumerateWindowsVsCodeCandidates())
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            foreach (var candidate in EnumeratePathCandidates("code.exe", "code-insiders.exe"))
            {
                return candidate;
            }
        }

        foreach (var commandName in new[] { "code", "code-insiders" })
        {
            if (ResolveExecutableOnPath(commandName) is { } path)
            {
                return path;
            }
        }

        throw new InvalidOperationException(
            "VS Code was not found on this runner. Install Visual Studio Code and ensure the 'code' command is available.");
    }

    private static IEnumerable<string> EnumerateWindowsVsCodeCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        return
        [
            Path.Combine(localAppData, "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(localAppData, "Programs", "Microsoft VS Code Insiders", "Code - Insiders.exe"),
            Path.Combine(programFiles, "Microsoft VS Code", "Code.exe"),
            Path.Combine(programFilesX86, "Microsoft VS Code", "Code.exe")
        ];
    }

    private static string? ResolveExecutableOnPath(string commandName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directCandidate = Path.Combine(directory, commandName);
            if (File.Exists(directCandidate))
            {
                return directCandidate;
            }

            if (OperatingSystem.IsWindows())
            {
                foreach (var extension in new[] { ".exe", ".cmd", ".bat" })
                {
                    var candidate = directCandidate + extension;
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumeratePathCandidates(params string[] commandNames)
    {
        foreach (var commandName in commandNames)
        {
            if (ResolveExecutableOnPath(commandName) is { } candidate)
            {
                yield return candidate;
            }
        }
    }

    private static async Task<JsonObject> ReadJsonObjectAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var node = await JsonNode.ParseAsync(
            stream,
            nodeOptions: null,
            new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            },
            cancellationToken: cancellationToken);
        return node as JsonObject
            ?? throw new InvalidOperationException($"'{path}' does not contain a JSON object.");
    }

    private static async Task WriteJsonAsync(string path, JsonObject root, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, root.ToJsonString(JsonOptions), cancellationToken);
    }

    private static void UpsertNamedObject(JsonArray array, string name, JsonObject replacement, string propertyName = "name")
    {
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonObject existing)
            {
                continue;
            }

            if (string.Equals(existing[propertyName]?.GetValue<string>(), name, StringComparison.Ordinal))
            {
                array[index] = replacement;
                return;
            }
        }

        array.Add(replacement);
    }

    private static string QuoteForPowerShell(string value) =>
        $"'{value.Replace("'", "''")}'";

    private static string BuildWindowTitle(string projectName) =>
        string.IsNullOrWhiteSpace(projectName)
            ? VsCodeWindowTitle
            : $"{projectName} - {VsCodeWindowTitle}";

    private async Task<RemoteViewerSession?> TryCreateDeviceViewerAsync(
        string orchestrationSessionId,
        OrchestrationJob job,
        CancellationToken cancellationToken)
    {
        if (!TryBuildDeviceViewerRequest(orchestrationSessionId, job, out var request))
        {
            return null;
        }

        var viewer = _viewers.Create(request);
        try
        {
            return await _viewerBootstrap.BootstrapAsync(viewer.Id, cancellationToken: cancellationToken) ?? viewer;
        }
        catch
        {
            CloseViewer(viewer.Id, "Device viewer bootstrap failed.");
            throw;
        }
    }

    private static bool TryBuildDeviceViewerRequest(
        string orchestrationSessionId,
        OrchestrationJob job,
        out CreateRemoteViewerSessionRequest request)
    {
        request = new CreateRemoteViewerSessionRequest();

        if (job.DeviceSelection?.HasTarget != true ||
            !TryMapDeviceViewerTargetKind(job.Platform, out var targetKind))
        {
            return false;
        }

        var deviceLabel = job.DeviceSelection.DisplayName ?? job.DeviceSelection.DeviceId ?? job.DeviceSelection.ProfileId ?? job.LaunchProfileName;
        request = new CreateRemoteViewerSessionRequest
        {
            MachineId = job.TargetMachineId,
            MachineName = job.TargetMachineName,
            JobId = job.Id,
            Provider = RemoteViewerProviderKind.Managed,
            Target = new RemoteViewerTarget
            {
                Kind = targetKind,
                DisplayName = $"{job.ProjectName} {deviceLabel}",
                JobId = job.Id,
                SessionId = orchestrationSessionId,
                VirtualDeviceId = job.DeviceSelection.DeviceId,
                VirtualDeviceProfileId = job.DeviceSelection.ProfileId
            }
        };

        return true;
    }

    private static bool TryMapDeviceViewerTargetKind(ApplicationTargetPlatform platform, out RemoteViewerTargetKind targetKind)
    {
        switch (platform)
        {
            case ApplicationTargetPlatform.Android:
                targetKind = RemoteViewerTargetKind.Emulator;
                return true;
            case ApplicationTargetPlatform.iOS:
                targetKind = RemoteViewerTargetKind.Simulator;
                return true;
            default:
                targetKind = default;
                return false;
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // Best effort shutdown for an external GUI process.
        }
        finally
        {
            process.Dispose();
        }
    }

    private void CloseViewer(string? viewerSessionId, string message)
    {
        if (string.IsNullOrWhiteSpace(viewerSessionId))
        {
            return;
        }

        _viewers.Close(viewerSessionId, message);
    }

    private void UpdateSession(string orchestrationSessionId, Func<VsCodeDebugSession, VsCodeDebugSession> update)
    {
        _sessionRecords.AddOrUpdate(
            orchestrationSessionId,
            _ => throw new InvalidOperationException($"No VS Code debug session exists for orchestration session '{orchestrationSessionId}'."),
            (_, existing) => update(existing));
    }

    private static VsCodeDebugSession WithStatus(
        VsCodeDebugSession session,
        VsCodeDebugSessionStatus status,
        string message,
        VsCodeDebuggerVisibilityState visibility)
    {
        return new VsCodeDebugSession
        {
            Id = session.Id,
            OrchestrationSessionId = session.OrchestrationSessionId,
            JobId = session.JobId,
            ProjectName = session.ProjectName,
            Platform = session.Platform,
            MachineId = session.MachineId,
            MachineName = session.MachineName,
            WorkspaceDirectory = session.WorkspaceDirectory,
            StartupProjectPath = session.StartupProjectPath,
            DebugConfigurationName = session.DebugConfigurationName,
            ViewerSessionId = session.ViewerSessionId,
            TargetDisplayName = session.TargetDisplayName,
            RequiredExtensions = session.RequiredExtensions,
            WorkspaceAssets = session.WorkspaceAssets,
            CreatedAt = session.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            Status = status,
            DebuggerVisibility = visibility,
            StatusMessage = message
        };
    }

    private static VsCodeDebugSession WithExtensions(VsCodeDebugSession session, bool isInstalled)
    {
        return new VsCodeDebugSession
        {
            Id = session.Id,
            OrchestrationSessionId = session.OrchestrationSessionId,
            JobId = session.JobId,
            ProjectName = session.ProjectName,
            Platform = session.Platform,
            MachineId = session.MachineId,
            MachineName = session.MachineName,
            WorkspaceDirectory = session.WorkspaceDirectory,
            StartupProjectPath = session.StartupProjectPath,
            DebugConfigurationName = session.DebugConfigurationName,
            ViewerSessionId = session.ViewerSessionId,
            TargetDisplayName = session.TargetDisplayName,
            WorkspaceAssets = session.WorkspaceAssets,
            CreatedAt = session.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            Status = session.Status,
            DebuggerVisibility = session.DebuggerVisibility,
            StatusMessage = session.StatusMessage,
            RequiredExtensions =
            [
                new VsCodeDebugExtensionRequirement
                {
                    ExtensionId = CSharpExtensionId,
                    DisplayName = CSharpExtensionDisplayName,
                    IsInstalled = isInstalled
                }
            ]
        };
    }
}
