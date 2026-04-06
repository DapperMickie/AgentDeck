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

    /// <summary>
    /// Fits the terminal to its container (after a layout frame) and returns the
    /// resulting dimensions. Use this instead of FitAsync when you need to send
    /// the actual size to the runner — it avoids the race where the PTY starts at
    /// the wrong column width before the resize event fires.
    /// </summary>
    public async Task<(int Cols, int Rows)?> FitAndGetSizeAsync(string sessionId)
    {
        var module = await _moduleTask.Value;
        var result = await module.InvokeAsync<JsonElement?>("fitAndGetSize", sessionId);
        if (result is null) return null;
        var cols = result.Value.GetProperty("cols").GetInt32();
        var rows = result.Value.GetProperty("rows").GetInt32();
        return (cols, rows);
    }

    public async Task FocusAsync(string sessionId)
    {
        var module = await _moduleTask.Value;
        await module.InvokeVoidAsync("focusTerminal", sessionId);
    }

    public async Task RegisterAutoFitAsync(string sessionId)
    {
        var module = await _moduleTask.Value;
        await module.InvokeVoidAsync("registerAutoFit", sessionId);
    }

    public async Task UnregisterAutoFitAsync(string sessionId)
    {
        var module = await _moduleTask.Value;
        await module.InvokeVoidAsync("unregisterAutoFit", sessionId);
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

    public async Task RegisterShortcutsAsync<T>(DotNetObjectReference<T> dotnetRef) where T : class
    {
        var module = await _moduleTask.Value;
        await module.InvokeVoidAsync("registerShortcuts", dotnetRef);
    }

    public async Task UnregisterShortcutsAsync()
    {
        var module = await _moduleTask.Value;
        await module.InvokeVoidAsync("unregisterShortcuts");
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
