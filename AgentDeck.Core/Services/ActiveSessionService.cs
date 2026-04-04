namespace AgentDeck.Core.Services;

public sealed class ActiveSessionService : IActiveSessionService
{
    public string? ActiveSessionId { get; private set; }
    public bool IsDialogOpen { get; private set; }

    public event EventHandler? StateChanged;

    public void SetActiveSession(string? id)
    {
        ActiveSessionId = id;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void OpenDialog()
    {
        IsDialogOpen = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CloseDialog()
    {
        IsDialogOpen = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
