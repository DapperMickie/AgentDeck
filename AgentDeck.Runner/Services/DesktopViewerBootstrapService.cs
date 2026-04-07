using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using AgentDeck.Shared.Enums;
using AgentDeck.Shared.Models;

namespace AgentDeck.Runner.Services;

/// <inheritdoc />
public sealed class DesktopViewerBootstrapService : IDesktopViewerBootstrapService
{
    private sealed record ActiveDesktopTransport(Process Process);

    private sealed record BootstrapOutcome(
        string ConnectionUri,
        string? AccessToken,
        string Message,
        Process? Process = null);

    private readonly ConcurrentDictionary<string, ActiveDesktopTransport> _activeTransports = new();
    private readonly IRemoteViewerSessionService _viewers;
    private readonly ILogger<DesktopViewerBootstrapService> _logger;

    public DesktopViewerBootstrapService(
        IRemoteViewerSessionService viewers,
        ILogger<DesktopViewerBootstrapService> logger)
    {
        _viewers = viewers;
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

        if (session.Target.Kind != RemoteViewerTargetKind.Desktop)
        {
            return session;
        }

        var preparingResult = _viewers.Update(sessionId, new UpdateRemoteViewerSessionRequest
        {
            Status = RemoteViewerSessionStatus.Preparing,
            Message = BuildPreparingMessage(session.Provider)
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
            _logger.LogWarning(ex, "Desktop viewer bootstrap failed for session {SessionId}", sessionId);
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
            RemoteViewerProviderKind.Rdp => BootstrapRdp(connectionHost),
            RemoteViewerProviderKind.ScreenSharing => BootstrapScreenSharing(connectionHost),
            RemoteViewerProviderKind.Vnc => await BootstrapVncAsync(session.Id, connectionHost, cancellationToken),
            RemoteViewerProviderKind.X11 => throw new InvalidOperationException("X11 window streaming is not part of the first desktop bootstrap slice."),
            RemoteViewerProviderKind.Wayland => throw new InvalidOperationException("Wayland capture bootstrap is not part of the first desktop bootstrap slice."),
            _ => throw new InvalidOperationException($"Viewer provider '{session.Provider}' is not supported for desktop bootstrap.")
        };
    }

    private static BootstrapOutcome BootstrapRdp(string connectionHost)
    {
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
            Message: $"Desktop viewer is ready via RDP at {connectionHost}:{rdpPort}.");
    }

    private static BootstrapOutcome BootstrapScreenSharing(string connectionHost)
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
            Message: $"Desktop viewer is ready via Screen Sharing at {connectionHost}:{vncPort}.");
    }

    private async Task<BootstrapOutcome> BootstrapVncAsync(
        string sessionId,
        string connectionHost,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            const int defaultVncPort = 5900;
            if (!IsTcpPortListening(defaultVncPort))
            {
                throw new InvalidOperationException(
                    "A VNC server is not listening on this runner. Start a VNC server before bootstrapping a desktop viewer.");
            }

            return new BootstrapOutcome(
                ConnectionUri: $"vnc://{connectionHost}:{defaultVncPort}",
                AccessToken: null,
                Message: $"Desktop viewer is ready via VNC at {connectionHost}:{defaultVncPort}.");
        }

        var display = Environment.GetEnvironmentVariable("DISPLAY");
        if (string.IsNullOrWhiteSpace(display))
        {
            throw new InvalidOperationException("Linux desktop bootstrap requires DISPLAY to be set.");
        }

        var x11vncPath = ResolveExecutableOnPath("x11vnc");
        if (x11vncPath is null)
        {
            throw new InvalidOperationException("Linux desktop bootstrap requires x11vnc to be installed on the runner.");
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
            ?? throw new InvalidOperationException("x11vnc did not start a desktop transport process.");

        if (!await WaitForPortOrExitAsync(process, port, cancellationToken))
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            TryKillProcess(process);

            var detail = FirstMeaningfulLine(error, output);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? "x11vnc exited before the desktop viewer transport became ready."
                : detail);
        }

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            if (_activeTransports.TryRemove(sessionId, out _))
            {
                _viewers.Update(sessionId, new UpdateRemoteViewerSessionRequest
                {
                    Status = RemoteViewerSessionStatus.Failed,
                    Message = "Desktop viewer transport exited unexpectedly."
                });
            }

            process.Dispose();
        };

        if (process.HasExited)
        {
            process.Dispose();
            throw new InvalidOperationException("x11vnc exited before the desktop viewer transport could be retained.");
        }

        return new BootstrapOutcome(
            ConnectionUri: $"vnc://{connectionHost}:{port}",
            AccessToken: accessToken,
            Message: $"Desktop viewer is ready via x11vnc at {connectionHost}:{port}.",
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
        throw new InvalidOperationException($"A desktop viewer transport is already active for session '{sessionId}'.");
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

    private static async Task<bool> WaitForPortOrExitAsync(Process process, int port, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - startedAt < TimeSpan.FromSeconds(10))
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

    private static string BuildPreparingMessage(RemoteViewerProviderKind provider) => provider switch
    {
        RemoteViewerProviderKind.Rdp => "Preparing Remote Desktop transport.",
        RemoteViewerProviderKind.ScreenSharing => "Preparing Screen Sharing transport.",
        RemoteViewerProviderKind.Vnc => "Preparing VNC desktop transport.",
        _ => "Preparing desktop viewer transport."
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
