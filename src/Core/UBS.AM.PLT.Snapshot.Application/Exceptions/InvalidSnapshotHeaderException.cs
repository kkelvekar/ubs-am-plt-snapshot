namespace UBS.AM.PLT.Snapshot.Application.Exceptions;

/// <summary>
/// Thrown when the completed snapshot's stored header has no usable value at
/// <c>$.Payload.Event</c>. Replaying the same header cannot repair this upstream contract
/// breach, so the snapshot is recorded as FAILED and the consumer commits past the message.
/// </summary>
public sealed class InvalidSnapshotHeaderException : SnapshotMessageRejectedException
{
    public const string InvalidHeaderEventReason = "INVALID_HEADER_EVENT";

    private InvalidSnapshotHeaderException()
        : base(
            InvalidHeaderEventReason,
            "Snapshot header has no usable value at '$.Payload.Event'; rejecting the completed snapshot.")
    {
    }

    public static InvalidSnapshotHeaderException InvalidEvent() => new();
}
