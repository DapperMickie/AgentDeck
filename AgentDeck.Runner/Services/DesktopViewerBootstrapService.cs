using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using AgentDeck.Runner.Configuration;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;
using Microsoft.Extensions.Options;

namespace AgentDeck.Runner.Services;

/// <inheritdoc />
public sealed class DesktopViewerBootstrapService : IDesktopViewerBootstrapService
{
    private sealed record ActiveDesktopTransport(Process Process);

    private sealed class ManagedViewerReadySignal
    {
        public string? ConnectionUri { get; init; }
        public string? AccessToken { get; init; }
        public string? Message { get; init; }
        public string? TargetKind { get; init; }
        public string? TargetDisplayName { get; init; }
        public string? TargetSessionId { get; init; }
        public string? TargetWindowTitle { get; init; }
        public string? TargetVirtualDeviceId { get; init; }
        public string? TargetVirtualDeviceProfileId { get; init; }
    }

    private sealed record BootstrapOutcome(
        string ConnectionUri,
        string? AccessToken,
        string Message,
        Process? Process = null);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, ActiveDesktopTransport> _activeTransports = new();
    private readonly IRemoteViewerSessionService _viewers;
    private readonly DesktopViewerTransportOptions _transportOptions;
    private readonly ILogger<DesktopViewerBootstrapService> _logger;

    public DesktopViewerBootstrapService(
        IRemoteViewerSessionService viewers,
        IOptions<DesktopViewerTransportOptions> transportOptions,
        ILogger<DesktopViewerBootstrapService> logger)
    {
        _viewers = viewers;
        _transportOptions = transportOptions.Value;
        _logger = logger;
    }

    public async Task<RemoteViewerSession?> BootstrapAsync(
        string sessionId,
        string? connectionHost = null,
        CancellationToken cancellationToken = default)
    {
        var session = _viewers.Get(sessionId);
        if (session is null)
        {
            return null;
        }

        var preparingResult = _viewers.Update(sessionId, new UpdateRemoteViewerSessionRequest
        {
            Status = RemoteViewerSessionStatus.Preparing,
            Message = BuildPreparingMessage(session)
        });

        session = preparingResult.Session ?? session;
        var connectionTarget = ResolveConnectionHost(connectionHost);

        try
        {
            var outcome = await BootstrapTransportAsync(session, connectionTarget, cancellationToken);
            if (outcome.Process is not null)
            {
                RegisterActiveTransport(sessionId, outcome.Process);
            }

            var updateResult = _viewers.Update(sessionId, new UpdateRemoteViewerSessionRequest
            {
                Status = RemoteViewerSessionStatus.Ready,
                ConnectionUri = outcome.ConnectionUri,
                AccessToken = outcome.AccessToken,
                Message = outcome.Message
            });

            if (updateResult.Session is null && outcome.Process is not null)
            {
                _activeTransports.TryRemove(sessionId, out _);
                TryKillProcess(outcome.Process);
            }

            return updateResult.Session ?? _viewers.Get(sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Viewer bootstrap failed for session {SessionId}", sessionId);
            var failedResult = _viewers.Update(sessionId, new UpdateRemoteViewerSessionRequest
            {
                Status = RemoteViewerSessionStatus.Failed,
                Message = ex.Message
            });

            return failedResult.Session ?? _viewers.Get(sessionId);
        }
    }

    public Task<RemoteViewerSessionMutationResult> CloseAsync(
        string sessionId,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        if (_activeTransports.TryRemove(sessionId, out var activeTransport))
        {
            TryKillProcess(activeTransport.Process);
        }

        return Task.FromResult(_viewers.Close(sessionId, message ?? "Viewer session closed."));
    }

    private async Task<BootstrapOutcome> BootstrapTransportAsync(
        RemoteViewerSession session,
        string connectionHost,
        CancellationToken cancellationToken)
    {
        return session.Provider switch
        {
            RemoteViewerProviderKind.Managed => await BootstrapManagedAsync(session, connectionHost, cancellationToken),
            RemoteViewerProviderKind.Rdp => BootstrapRdp(session, connectionHost),
            RemoteViewerProviderKind.ScreenSharing => BootstrapScreenSharing(session, connectionHost),
            RemoteViewerProviderKind.Vnc => await BootstrapVncAsync(session, connectionHost, cancellationToken),
            RemoteViewerProviderKind.X11 => throw new InvalidOperationException("X11 window streaming is not part of the first viewer bootstrap slice."),
            RemoteViewerProviderKind.Wayland => throw new InvalidOperationException("Wayland capture bootstrap is not part of the first viewer bootstrap slice."),
            _ => throw new InvalidOperationException($"Viewer provider '{session.Provider}' is not supported for viewer bootstrap.")
        };
    }

    private async Task<BootstrapOutcome> BootstrapManagedAsync(
        RemoteViewerSession session,
        string connectionHost,
        CancellationToken cancellationToken)
    {
        var options = _transportOptions.Managed;
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException(
                "AgentDeck-managed viewer transport is not configured on this runner. Set DesktopViewerTransport:Managed options before requesting the managed provider.");
        }

        var port = ReserveTcpPort();
        var accessToken = options.IssueAccessToken
            ? Convert.ToHexString(RandomNumberGenerator.GetBytes(Math.Max(1, options.AccessTokenBytes))).ToLowerInvariant()
            : null;
        var baseReplacements = BuildManagedBootstrapReplacements(session, connectionHost, port, accessToken);
        var readySignalPath = ResolveReadySignalPath(options.ReadySignalPathTemplate, baseReplacements);
        var replacements = BuildManagedBootstrapReplacements(session, connectionHost, port, accessToken, readySignalPath);
        DeleteReadySignalFile(readySignalPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = options.Command!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
        {
            startInfo.WorkingDirectory = options.WorkingDirectory;
        }

        foreach (var argument in options.Arguments)
        {
            startInfo.ArgumentList.Add(ApplyTemplate(argument, replacements));
        }

        foreach (var (key, value) in options.EnvironmentVariables)
        {
            startInfo.Environment[key] = ApplyTemplate(value, replacements);
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Managed viewer transport helper did not start.");

        ManagedViewerReadySignal? readySignal = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(readySignalPath))
            {
                readySignal = await WaitForReadySignalOrExitAsync(process, readySignalPath, options.StartupTimeout, cancellationToken);
                ValidateReadySignal(session, readySignal);
            }
            else if (!await WaitForPortOrExitAsync(process, port, options.StartupTimeout, cancellationToken))
            {
                throw await BuildManagedBootstrapFailureAsync(process, cancellationToken);
            }
        }
        catch
        {
            TryKillProcess(process);
            throw;
        }

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            if (_activeTransports.TryRemove(session.Id, out _))
            {
                _viewers.Update(session.Id, new UpdateRemoteViewerSessionRequest
                {
                    Status = RemoteViewerSessionStatus.Failed,
                    Message = $"Managed {GetTargetLabel(session.Target.Kind)} transport exited unexpectedly."
                });
            }

            process.Dispose();
        };

        if (process.HasExited)
        {
            process.Dispose();
            throw new InvalidOperationException("Managed viewer transport exited before it could be retained.");
        }

        return new BootstrapOutcome(
            ConnectionUri: string.IsNullOrWhiteSpace(readySignal?.ConnectionUri)
                ? ApplyTemplate(options.ConnectionUriTemplate!, replacements)
                : readySignal.ConnectionUri,
            AccessToken: string.IsNullOrWhiteSpace(readySignal?.AccessToken) ? accessToken : readySignal.AccessToken,
            Message: string.IsNullOrWhiteSpace(readySignal?.Message)
                ? $"{GetTargetDisplayName(session)} is ready via AgentDeck-managed transport at {connectionHost}:{port}."
                : readySignal.Message,
            Process: process);
    }

    private static Dictionary<string, string> BuildManagedBootstrapReplacements(
        RemoteViewerSession session,
        string connectionHost,
        int port,
        string? accessToken,
        string? readySignalPath = null)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sessionId"] = session.Id,
            ["port"] = port.ToString(),
            ["token"] = accessToken ?? string.Empty,
            ["host"] = connectionHost,
            ["machineName"] = Environment.MachineName,
            ["targetKind"] = session.Target.Kind.ToString(),
            ["targetDisplayName"] = session.Target.DisplayName,
            ["targetJobId"] = session.Target.JobId ?? string.Empty,
            ["targetSessionId"] = session.Target.SessionId ?? string.Empty,
            ["targetWindowTitle"] = session.Target.WindowTitle ?? string.Empty,
            ["targetVirtualDeviceId"] = session.Target.VirtualDeviceId ?? string.Empty,
            ["targetVirtualDeviceProfileId"] = session.Target.VirtualDeviceProfileId ?? string.Empty,
            ["readySignalPath"] = readySignalPath ?? string.Empty
        };
    }

    private static string? ResolveReadySignalPath(string? template, IReadOnlyDictionary<string, string> replacements) =>
        string.IsNullOrWhiteSpace(template) ? null : ApplyTemplate(template, replacements);

    private static void DeleteReadySignalFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static async Task<ManagedViewerReadySignal> WaitForReadySignalOrExitAsync(
        Process process,
        string readySignalPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        string? lastError = null;

        while (DateTimeOffset.UtcNow - startedAt < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryReadReadySignal(readySignalPath, out var signal, out var error))
            {
                return signal!;
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                lastError = error;
            }

            if (process.HasExited)
            {
                throw new InvalidOperationException(lastError ?? $"Managed viewer transport exited before publishing ready signal '{readySignalPath}'.");
            }

            await Task.Delay(250, cancellationToken);
        }

        if (TryReadReadySignal(readySignalPath, out var finalSignal, out var finalError))
        {
            return finalSignal!;
        }

        throw new InvalidOperationException(
            !string.IsNullOrWhiteSpace(finalError)
                ? finalError
                : $"Managed viewer transport did not publish ready signal '{readySignalPath}' within {timeout}.");
    }

    private static bool TryReadReadySignal(
        string readySignalPath,
        out ManagedViewerReadySignal? signal,
        out string? error)
    {
        signal = null;
        error = null;

        if (!File.Exists(readySignalPath))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(readySignalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            signal = JsonSerializer.Deserialize<ManagedViewerReadySignal>(json, JsonOptions)
                ?? throw new InvalidOperationException("Ready signal file was empty.");
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (Exception ex)
        {
            error = $"Managed viewer transport wrote an invalid ready signal at '{readySignalPath}': {ex.Message}";
            return false;
        }
    }

    private static void ValidateReadySignal(RemoteViewerSession session, ManagedViewerReadySignal signal)
    {
        if (string.IsNullOrWhiteSpace(signal.TargetKind) ||
            !Enum.TryParse<RemoteViewerTargetKind>(signal.TargetKind, ignoreCase: true, out var signaledTargetKind))
        {
            throw new InvalidOperationException("Managed viewer ready signal did not report a valid target kind.");
        }

        if (signaledTargetKind != session.Target.Kind)
        {
            throw new InvalidOperationException(
                $"Managed viewer ready signal reported target kind '{signaledTargetKind}', but the session requested '{session.Target.Kind}'.");
        }

        ValidateReadySignalField("target display name", session.Target.DisplayName, signal.TargetDisplayName);
        ValidateReadySignalField("target session id", session.Target.SessionId, signal.TargetSessionId);
        ValidateReadySignalField("target window title", session.Target.WindowTitle, signal.TargetWindowTitle);
        ValidateReadySignalField("target virtual device id", session.Target.VirtualDeviceId, signal.TargetVirtualDeviceId);
        ValidateReadySignalField("target virtual device profile id", session.Target.VirtualDeviceProfileId, signal.TargetVirtualDeviceProfileId);
    }

    private static void ValidateReadySignalField(string fieldName, string? requestedValue, string? signaledValue)
    {
        if (string.IsNullOrWhiteSpace(signaledValue))
        {
            return;
        }

        var expectedValue = requestedValue ?? string.Empty;
        if (!string.Equals(expectedValue, signaledValue, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Managed viewer ready signal reported {fieldName} '{signaledValue}', but the session requested '{expectedValue}'.");
        }
    }

    private static async Task<InvalidOperationException> BuildManagedBootstrapFailureAsync(Process process, CancellationToken cancellationToken)
    {
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        TryKillProcess(process);

        var detail = FirstMeaningfulLine(error, output);
        return new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
            ? "Managed viewer transport exited before it became ready."
            : detail);
    }

    private static BootstrapOutcome BootstrapRdp(RemoteViewerSession session, string connectionHost)
    {
        if (session.Target.Kind != RemoteViewerTargetKind.Desktop)
        {
            throw new InvalidOperationException($"RDP only supports desktop targets, but this session requested '{session.Target.Kind}'.");
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("RDP desktop bootstrap is only supported on Windows runners.");
        }

        const int rdpPort = 3389;
        if (!IsTcpPortListening(rdpPort))
        {
            throw new InvalidOperationException(
                "Remote Desktop is not ready on this runner. Enable Remote Desktop so TCP port 3389 is listening.");
        }

        return new BootstrapOutcome(
            ConnectionUri: $"rdp://{connectionHost}:{rdpPort}",
            AccessToken: null,
            Message: $"{GetTargetDisplayName(session)} is ready via RDP at {connectionHost}:{rdpPort}.");
    }

    private static BootstrapOutcome BootstrapScreenSharing(RemoteViewerSession session, string connectionHost)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new InvalidOperationException("Screen Sharing bootstrap is only supported on macOS runners.");
        }

        const int vncPort = 5900;
        if (!IsTcpPortListening(vncPort))
        {
            throw new InvalidOperationException(
                "macOS Screen Sharing is not ready on this runner. Enable Screen Sharing so TCP port 5900 is listening.");
        }

        return new BootstrapOutcome(
            ConnectionUri: $"vnc://{connectionHost}:{vncPort}",
            AccessToken: null,
            Message: $"{GetTargetDisplayName(session)} is ready via Screen Sharing at {connectionHost}:{vncPort}.");
    }

    private async Task<BootstrapOutcome> BootstrapVncAsync(
        RemoteViewerSession session,
        string connectionHost,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            const int defaultVncPort = 5900;
            if (!IsTcpPortListening(defaultVncPort))
            {
                throw new InvalidOperationException(
                    $"A VNC server is not listening on this runner. Start a VNC server before bootstrapping {GetTargetLabel(session.Target.Kind)}.");
            }

            return new BootstrapOutcome(
                ConnectionUri: $"vnc://{connectionHost}:{defaultVncPort}",
                AccessToken: null,
                Message: $"{GetTargetDisplayName(session)} is ready via VNC at {connectionHost}:{defaultVncPort}.");
        }

        var display = Environment.GetEnvironmentVariable("DISPLAY");
        if (string.IsNullOrWhiteSpace(display))
        {
            throw new InvalidOperationException($"Linux {GetTargetLabel(session.Target.Kind)} bootstrap requires DISPLAY to be set.");
        }

        var x11vncPath = ResolveExecutableOnPath("x11vnc");
        if (x11vncPath is null)
        {
            throw new InvalidOperationException($"Linux {GetTargetLabel(session.Target.Kind)} bootstrap requires x11vnc to be installed on the runner.");
        }

        var port = ReserveTcpPort();
        var accessToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var startInfo = new ProcessStartInfo
        {
            FileName = x11vncPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-display");
        startInfo.ArgumentList.Add(display);
        startInfo.ArgumentList.Add("-forever");
        startInfo.ArgumentList.Add("-shared");
        startInfo.ArgumentList.Add("-rfbport");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add("-passwd");
        startInfo.ArgumentList.Add(accessToken);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"x11vnc did not start a {GetTargetLabel(session.Target.Kind)} transport process.");

        if (!await WaitForPortOrExitAsync(process, port, TimeSpan.FromSeconds(10), cancellationToken))
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            TryKillProcess(process);

            var detail = FirstMeaningfulLine(error, output);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? $"x11vnc exited before the {GetTargetLabel(session.Target.Kind)} transport became ready."
                : detail);
        }

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            if (_activeTransports.TryRemove(session.Id, out _))
            {
                _viewers.Update(session.Id, new UpdateRemoteViewerSessionRequest
                {
                    Status = RemoteViewerSessionStatus.Failed,
                    Message = $"{GetTargetDisplayName(session)} transport exited unexpectedly."
                });
            }

            process.Dispose();
        };

        if (process.HasExited)
        {
            process.Dispose();
            throw new InvalidOperationException($"x11vnc exited before the {GetTargetLabel(session.Target.Kind)} transport could be retained.");
        }

        return new BootstrapOutcome(
            ConnectionUri: $"vnc://{connectionHost}:{port}",
            AccessToken: accessToken,
            Message: $"{GetTargetDisplayName(session)} is ready via x11vnc at {connectionHost}:{port}.",
            Process: process);
    }

    private void RegisterActiveTransport(string sessionId, Process process)
    {
        var activeTransport = new ActiveDesktopTransport(process);
        if (_activeTransports.TryAdd(sessionId, activeTransport))
        {
            return;
        }

        TryKillProcess(process);
        throw new InvalidOperationException($"A viewer transport is already active for session '{sessionId}'.");
    }

    private static bool IsTcpPortListening(int port)
    {
        return IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(endpoint => endpoint.Port == port);
    }

    private static int ReserveTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<bool> WaitForPortOrExitAsync(Process process, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - startedAt < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsTcpPortListening(port))
            {
                return true;
            }

            if (process.HasExited)
            {
                return false;
            }

            await Task.Delay(250, cancellationToken);
        }

        return IsTcpPortListening(port);
    }

    private static string ApplyTemplate(string template, IReadOnlyDictionary<string, string> replacements)
    {
        var value = template;
        foreach (var (key, replacement) in replacements)
        {
            value = value.Replace($"{{{key}}}", replacement, StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }

    private static string ResolveConnectionHost(string? preferredHost)
    {
        if (!string.IsNullOrWhiteSpace(preferredHost) &&
            !string.Equals(preferredHost, "0.0.0.0", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(preferredHost, "[::]", StringComparison.OrdinalIgnoreCase))
        {
            return preferredHost;
        }

        try
        {
            var hostName = Dns.GetHostName();
            if (!string.IsNullOrWhiteSpace(hostName))
            {
                return hostName;
            }
        }
        catch
        {
        }

        return Environment.MachineName;
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
            var candidate = Path.Combine(directory, commandName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string GetTargetDisplayName(RemoteViewerSession session) =>
        string.IsNullOrWhiteSpace(session.Target.DisplayName)
            ? GetTargetLabel(session.Target.Kind)
            : session.Target.DisplayName;

    private static string GetTargetLabel(RemoteViewerTargetKind targetKind) => targetKind switch
    {
        RemoteViewerTargetKind.Desktop => "desktop viewer",
        RemoteViewerTargetKind.Window => "window viewer",
        RemoteViewerTargetKind.VsCode => "VS Code viewer",
        RemoteViewerTargetKind.Emulator => "emulator viewer",
        RemoteViewerTargetKind.Simulator => "simulator viewer",
        _ => "viewer"
    };

    private static string BuildPreparingMessage(RemoteViewerSession session) => session.Provider switch
    {
        RemoteViewerProviderKind.Managed => $"Preparing AgentDeck-managed {GetTargetLabel(session.Target.Kind)} transport.",
        RemoteViewerProviderKind.Rdp => "Preparing Remote Desktop transport.",
        RemoteViewerProviderKind.ScreenSharing => "Preparing Screen Sharing transport.",
        RemoteViewerProviderKind.Vnc => "Preparing VNC desktop transport.",
        _ => "Preparing viewer transport."
    };

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
        }
        finally
        {
            process.Dispose();
        }
    }
}
