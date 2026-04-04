using System.Text.Json;
using Microsoft.JSInterop;

namespace AgentDeck.Core.Services;

/// <summary>Wraps the agentdeck.js ES module for terminal lifecycle management.</summary>
public sealed class TerminalInterop : IAsyncDisposable
{
    private readonly Lazy<Task<IJSObjectReference>> _moduleTask;

    public TerminalInterop(IJSRuntime js)
    {
        _moduleTask = new Lazy<Task<IJSObjectReference>>(
            () => js.InvokeAsync<IJSObjectReference>(
                "import", "./_content/AgentDeck.Core/js/agentdeck.js").AsTask(),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task CreateTerminalAsync<T>(string elementId, string sessionId, DotNetObjectReference<T> dotnetRef)
        where T : class
    {
        var module = await _moduleTask.Value;
        await module.InvokeVoidAsync("createTerminal", elementId, sessionId, dotnetRef);
    }

    public async Task WriteAsync(string sessionId, string data)
    {
        var module = await _moduleTask.Value;
        await module.InvokeVoidAsync("writeToTerminal", sessionId, data);
    }

    public async Task FitAsync(string sessionId)
    {
        var module = await _moduleTask.Value;
        await module.InvokeVoidAsync("fitTerminal", sessionId);
    }

    public async Task FocusAsync(string sessionId)
    {
        var module = await _moduleTask.Value;
        await module.InvokeVoidAsync("focusTerminal", sessionId);
    }

    public async Task DisposeTerminalAsync(string sessionId)
    {
        var module = await _moduleTask.Value;
        await module.InvokeVoidAsync("disposeTerminal", sessionId);
    }

    public async Task<(int Cols, int Rows)?> GetSizeAsync(string sessionId)
    {
        var module = await _moduleTask.Value;
        var result = await module.InvokeAsync<JsonElement?>("getTerminalSize", sessionId);
        if (result is null) return null;
        var cols = result.Value.GetProperty("cols").GetInt32();
        var rows = result.Value.GetProperty("rows").GetInt32();
        return (cols, rows);
    }

    public async ValueTask DisposeAsync()
    {
        if (_moduleTask.IsValueCreated)
        {
            var module = await _moduleTask.Value;
            await module.DisposeAsync();
        }
    }
}
