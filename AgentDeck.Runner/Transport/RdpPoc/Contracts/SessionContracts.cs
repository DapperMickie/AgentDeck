namespace RdpPoc.Contracts;

public enum RemoteSessionState
{
    Requested = 0,
    Connecting = 1,
    Active = 2,
    Closing = 3,
    Closed = 4,
    Failed = 5,
}

public sealed record CreateSessionRequest(
    string HostId,
    CaptureTargetKind TargetKind,
    string? TargetId,
    string ViewerName);

public sealed record RemoteSessionSummary(
    string SessionId,
    string HostId,
    string HostName,
    CaptureTargetKind TargetKind,
    string TargetId,
    string TargetDisplayName,
    string ViewerName,
    RemoteSessionState State,
    DateTimeOffset CreatedAt,
    string RelayPath,
    DateTimeOffset? LastFrameAt);

public sealed record CreateSessionResponse(
    RemoteSessionSummary Session,
    string ViewerAccessToken,
    string RelayHubPath);

public sealed record HostSessionAssignment(
    string SessionId,
    CaptureTargetKind TargetKind,
    string TargetId,
    string TargetDisplayName,
    string ViewerName,
    string HostAccessToken);

public sealed record RelayFrame(
    string SessionId,
    long SequenceId,
    DateTimeOffset CapturedAt,
    string ContentType,
    int Width,
    int Height,
    byte[] Payload);

public sealed record PointerInputEvent(
    string SessionId,
    string EventType,
    double X,
    double Y,
    string? Button,
    int ClickCount = 0,
    int WheelDeltaX = 0,
    int WheelDeltaY = 0);

public sealed record KeyboardInputEvent(
    string SessionId,
    string EventType,
    string Code,
    bool Alt,
    bool Control,
    bool Shift);

public sealed record CloseSessionRequest(string ViewerAccessToken);
