namespace AgentDeck.Runner.Services;

/// <summary>Manages pseudo-terminal (PTY) process instances.</summary>
public interface IPtyProcessManager : IAsyncDisposable
{
    event EventHandler<(string SessionId, string Data)>? OutputReceived;
    event EventHandler<(string SessionId, int ExitCode)>? ProcessExited;

    Task StartAsync(string sessionId, string command, string workingDirectory, int cols, int rows, CancellationToken cancellationToken = default);
    Task WriteAsync(string sessionId, string data, CancellationToken cancellationToken = default);
    Task ResizeAsync(string sessionId, int cols, int rows, CancellationToken cancellationToken = default);
    Task KillAsync(string sessionId, CancellationToken cancellationToken = default);
    bool IsActive(string sessionId);
}
