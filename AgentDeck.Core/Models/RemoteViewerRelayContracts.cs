namespace AgentDeck.Core.Models;

public sealed record RemoteViewerRelayFrame(
    string SessionId,
    long SequenceId,
    DateTimeOffset CapturedAt,
    string ContentType,
    int Width,
    int Height,
    byte[] Payload);

public sealed record RemoteViewerPointerInputEvent(
    string SessionId,
    string EventType,
    double X,
    double Y,
    string? Button,
    int ClickCount = 0,
    int WheelDeltaX = 0,
    int WheelDeltaY = 0);

public sealed record RemoteViewerKeyboardInputEvent(
    string SessionId,
    string EventType,
    string Code,
    bool Alt,
    bool Control,
    bool Shift);
