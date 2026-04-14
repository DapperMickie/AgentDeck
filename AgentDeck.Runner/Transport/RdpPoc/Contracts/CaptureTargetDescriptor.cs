namespace RdpPoc.Contracts;

public sealed record CaptureTargetDescriptor(
    string Id,
    string DisplayName,
    CaptureTargetKind Kind,
    int? OwnerProcessId = null);
