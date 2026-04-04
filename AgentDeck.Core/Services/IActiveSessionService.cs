namespace AgentDeck.Core.Services;

/// <summary>
/// Scoped service that tracks which terminal session is currently selected
/// and whether the new-terminal dialog is open. Allows MainLayout (sidebar)
/// and Index (terminal dashboard) to stay in sync without tight coupling.
/// </summary>
public interface IActiveSessionService
{
    string? ActiveSessionId { get; }
    bool IsDialogOpen { get; }

    void SetActiveSession(string? id);
    void OpenDialog();
    void CloseDialog();

    event EventHandler? StateChanged;
}
