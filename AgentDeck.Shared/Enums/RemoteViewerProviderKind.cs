namespace AgentDeck.Shared.Enums;

/// <summary>Provider/transport family used to expose a remote viewer session.</summary>
public enum RemoteViewerProviderKind
{
    Auto,
    Vnc,
    Rdp,
    ScreenSharing,
    X11,
    Wayland
}
