namespace AgentDeck.Core.Services;

public interface IToastService
{
    void Show(string message, ToastKind kind = ToastKind.Info);
    event Action<ToastMessage> OnToast;
}

public enum ToastKind { Info, Success, Warning, Error }

public record ToastMessage(string Id, string Message, ToastKind Kind);
