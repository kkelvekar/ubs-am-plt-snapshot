using UBS.AM.PLT.Snapshot.Domain.Entities;

namespace UBS.AM.PLT.Snapshot.Domain;

/// <summary>
/// Outbound counterpart to <see cref="SnapshotMessage"/>: what this service tells the
/// publishing application about a snapshot's progress. Emitted when a snapshot starts
/// arriving (RECEIVING), when it reaches COMPLETE, and when a message is rejected (FAILED,
/// carrying <see cref="ReasonCode"/> and <see cref="ReasonDetail"/>).
/// </summary>
/// <remarks>
/// Strongly typed: <see cref="Status"/> is the domain enum and the timestamps are
/// <see cref="DateTime"/>. Rendering them as the strings the wire contract expects is
/// Infrastructure's job, so the transport format can change without touching the Domain.
/// </remarks>
public sealed record SnapshotStatusNotification
{
    public required string SnapshotId { get; init; }

    public required string AccountId { get; init; }

    /// <summary>File names already stored for this snapshot (e.g. <c>header.json</c>), not payload types.</summary>
    public required IReadOnlyList<string> ReceivedFiles { get; init; }

    /// <summary>Required file names still outstanding; empty once the snapshot is complete.</summary>
    public required IReadOnlyList<string> MissingFiles { get; init; }

    public required SnapshotTrackingStatus Status { get; init; }

    public required DateTime FirstReceivedAt { get; init; }

    public required DateTime LastUpdatedAt { get; init; }

    /// <summary>Null until the snapshot completes.</summary>
    public DateTime? CompletedAt { get; init; }

    /// <summary>Null unless the snapshot was declared failed.</summary>
    public DateTime? DeclaredFailedAt { get; init; }

    /// <summary>Machine-readable rejection cause (e.g. "FIELD_TOO_LONG"); empty unless Status is Failed due to a rejection.</summary>
    public string ReasonCode { get; init; } = string.Empty;

    /// <summary>Human-readable rejection detail; empty unless Status is Failed due to a rejection.</summary>
    public string ReasonDetail { get; init; } = string.Empty;
}
