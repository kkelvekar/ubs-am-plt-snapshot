namespace UBS.AM.PLT.Snapshot.Domain;

/// <summary>
/// Input for recording a rejected message against its snapshot's tracking row: the identity
/// the row is keyed and described by, plus why the message was refused.
/// </summary>
/// <remarks>
/// Deliberately not a <see cref="SnapshotMessage"/>, because the message it describes was
/// rejected for having an unusable envelope. <see cref="SnapshotId"/> is the only field the
/// caller guarantees — a null or over-long snapshotId is not storable, so no record is built
/// for it. The descriptive fields are nullable because a null value is itself a rejection
/// reason, and the store normalises whatever cannot be persisted.
/// </remarks>
public sealed record SnapshotRejectionRecord
{
    public required string SnapshotId { get; init; }

    public required string? AccountId { get; init; }

    public required string? SnapshotType { get; init; }

    /// <summary>Stable machine-readable cause, e.g. <c>FIELD_TOO_LONG</c>.</summary>
    public required string ReasonCode { get; init; }

    /// <summary>Human-readable detail — the rejection exception's message.</summary>
    public required string ReasonDetail { get; init; }
}
