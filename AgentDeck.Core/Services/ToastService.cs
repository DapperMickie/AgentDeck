namespace AgentDeck.Core.Services;

public sealed class ToastService : IToastService
{
    public event Action<ToastMessage>? OnToast;

    public void Show(string message, ToastKind kind = ToastKind.Info)
    {
        OnToast?.Invoke(new ToastMessage(Guid.NewGuid().ToString(), message, kind));
    }
}
