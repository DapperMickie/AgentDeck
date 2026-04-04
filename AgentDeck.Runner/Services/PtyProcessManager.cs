using System.Collections.Concurrent;
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

    public async Task StartAsync(string sessionId, string command, string workingDirectory, int cols, int rows, CancellationToken cancellationToken = default)
    {
        var options = new PtyOptions
        {
            Name = sessionId,
            Cols = cols,
            Rows = rows,
            Cwd = workingDirectory,
            App = command,
        };

        var connection = await PtyProvider.SpawnAsync(options, cancellationToken);
        var cts = new CancellationTokenSource();

        connection.ProcessExited += (_, e) =>
        {
            _logger.LogDebug("PTY process exited for session {SessionId} with code {ExitCode}", sessionId, e.ExitCode);
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
}
