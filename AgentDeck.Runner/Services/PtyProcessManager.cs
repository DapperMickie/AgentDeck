using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Porta.Pty;

namespace AgentDeck.Runner.Services;

/// <inheritdoc />
public sealed class PtyProcessManager : IPtyProcessManager
{
    private sealed record PtyEntry(IPtyConnection Connection, CancellationTokenSource Cts);

    private readonly ConcurrentDictionary<string, PtyEntry> _connections = new();
    private readonly ILogger<PtyProcessManager> _logger;

    public event EventHandler<(string SessionId, string Data)>? OutputReceived;
    public event EventHandler<(string SessionId, int ExitCode)>? ProcessExited;

    public PtyProcessManager(ILogger<PtyProcessManager> logger)
    {
        _logger = logger;
    }

    public async Task StartAsync(string sessionId, string command, IReadOnlyList<string> arguments, string workingDirectory, int cols, int rows, CancellationToken cancellationToken = default)
    {
        var commandLine = arguments.Count > 0
            ? [command, .. arguments]
            : (string[])[command];

        var options = new PtyOptions
        {
            Name = sessionId,
            Cols = cols,
            Rows = rows,
            Cwd = workingDirectory,
            App = command,
            CommandLine = commandLine,
        };

        LogPtyPreflight(sessionId, command, workingDirectory, commandLine);

        _logger.LogInformation(
            "Starting PTY process for session {SessionId}: app={App}, cwd={WorkingDirectory}, cols={Cols}, rows={Rows}, commandLine={CommandLine}",
            sessionId,
            command,
            workingDirectory,
            cols,
            rows,
            string.Join(" ", commandLine));

        IPtyConnection connection;
        try
        {
            connection = await PtyProvider.SpawnAsync(options, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to spawn PTY process for session {SessionId}: app={App}, cwd={WorkingDirectory}, cols={Cols}, rows={Rows}, commandLine={CommandLine}",
                sessionId,
                command,
                workingDirectory,
                cols,
                rows,
                string.Join(" ", commandLine));
            throw;
        }

        var cts = new CancellationTokenSource();

        connection.ProcessExited += (_, e) =>
        {
            _logger.LogInformation(
                "PTY process exited for session {SessionId} with code {ExitCode}: app={App}, cwd={WorkingDirectory}, commandLine={CommandLine}",
                sessionId,
                e.ExitCode,
                command,
                workingDirectory,
                string.Join(" ", commandLine));
            ProcessExited?.Invoke(this, (sessionId, e.ExitCode));
            cts.Cancel();
        };

        var entry = new PtyEntry(connection, cts);
        _connections[sessionId] = entry;

        _ = Task.Run(() => ReadOutputLoopAsync(sessionId, connection, cts.Token), CancellationToken.None);
    }

    public async Task WriteAsync(string sessionId, string data, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(sessionId, out var entry))
        {
            _logger.LogWarning("WriteAsync: session {SessionId} not found", sessionId);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(data);
        await entry.Connection.WriterStream.WriteAsync(bytes, cancellationToken);
        await entry.Connection.WriterStream.FlushAsync(cancellationToken);
    }

    public Task ResizeAsync(string sessionId, int cols, int rows, CancellationToken cancellationToken = default)
    {
        if (_connections.TryGetValue(sessionId, out var entry))
            entry.Connection.Resize(cols, rows);
        else
            _logger.LogWarning("ResizeAsync: session {SessionId} not found", sessionId);

        return Task.CompletedTask;
    }

    public Task KillAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (_connections.TryRemove(sessionId, out var entry))
        {
            entry.Cts.Cancel();
            try { entry.Connection.Kill(); } catch (Exception ex) { _logger.LogDebug(ex, "Kill failed for session {SessionId}", sessionId); }
            entry.Connection.Dispose();
            entry.Cts.Dispose();
        }

        return Task.CompletedTask;
    }

    public bool IsActive(string sessionId) => _connections.ContainsKey(sessionId);

    public async ValueTask DisposeAsync()
    {
        foreach (var sessionId in _connections.Keys.ToList())
            await KillAsync(sessionId);
    }

    private async Task ReadOutputLoopAsync(string sessionId, IPtyConnection connection, CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int bytesRead = await connection.ReaderStream.ReadAsync(buffer, ct);
                if (bytesRead == 0) break;

                var data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                OutputReceived?.Invoke(this, (sessionId, data));
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Output read loop ended for session {SessionId}", sessionId);
        }
    }

    private void LogPtyPreflight(string sessionId, string command, string workingDirectory, IReadOnlyList<string> commandLine)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var resolvedCommandPath = ResolveCommandPath(command);
        var commandMetadata = DescribePath(resolvedCommandPath ?? command);
        var workingDirectoryMetadata = DescribeDirectory(workingDirectory);
        var ptmxMetadata = DescribePath("/dev/ptmx");
        var ptsMetadata = DescribeDirectory("/dev/pts");

        _logger.LogInformation(
            "Linux PTY preflight for session {SessionId}: user={User}, processPath={ProcessPath}, os={OsDescription}, command={Command}, resolvedCommandPath={ResolvedCommandPath}, commandMetadata={CommandMetadata}, workingDirectoryMetadata={WorkingDirectoryMetadata}, devPtmx={DevPtmx}, devPts={DevPts}, commandLine={CommandLine}",
            sessionId,
            Environment.UserName,
            Environment.ProcessPath ?? "<unknown>",
            RuntimeInformation.OSDescription,
            command,
            resolvedCommandPath ?? "<not resolved>",
            commandMetadata,
            workingDirectoryMetadata,
            ptmxMetadata,
            ptsMetadata,
            string.Join(" ", commandLine));
    }

    private static string? ResolveCommandPath(string command)
    {
        if (Path.IsPathRooted(command))
        {
            return File.Exists(command) ? command : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, command);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string DescribeDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return $"exists=false path={path}";
        }

        var entries = SafeEnumerate(path).Take(5).ToArray();
        var sample = entries.Length == 0 ? "<empty>" : string.Join(", ", entries.Select(Path.GetFileName));
        return $"exists=true path={path} sampleEntries={sample}";
    }

    private static string DescribePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return $"exists=false path={path}";
        }

        var fileExists = File.Exists(path);
        var directoryExists = Directory.Exists(path);
        var unixMode = TryGetUnixFileMode(path);

        return $"exists=true path={path} isFile={fileExists} isDirectory={directoryExists} unixMode={unixMode}";
    }

    private static string TryGetUnixFileMode(string path)
    {
        try
        {
            return File.GetUnixFileMode(path).ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return $"unavailable:{ex.GetType().Name}";
        }
    }

    private static IEnumerable<string> SafeEnumerate(string path)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [$"<unavailable:{ex.GetType().Name}>"];
        }
    }
}
