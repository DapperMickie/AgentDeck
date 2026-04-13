using Microsoft.JSInterop;

namespace AgentDeck.Core.Services;

public sealed class RemoteViewerInterop : IAsyncDisposable
{
    private readonly Lazy<Task<IJSObjectReference>> _moduleTask;

    public RemoteViewerInterop(IJSRuntime js)
    {
        _moduleTask = new Lazy<Task<IJSObjectReference>>(
            () => js.InvokeAsync<IJSObjectReference>(
                "import", "./_content/AgentDeck.Core/js/viewerInterop.js").AsTask(),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task AttachAsync<T>(string elementId, DotNetObjectReference<T> dotNetReference)
        where T : class
    {
        var module = await _moduleTask.Value;
        await module.InvokeVoidAsync("attach", elementId, dotNetReference);
    }

    public async Task DetachAsync(string elementId)
    {
        var module = await _moduleTask.Value;
        await module.InvokeVoidAsync("detach", elementId);
    }

    public async Task FocusAsync(string elementId)
    {
        var module = await _moduleTask.Value;
        await module.InvokeVoidAsync("focus", elementId);
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
