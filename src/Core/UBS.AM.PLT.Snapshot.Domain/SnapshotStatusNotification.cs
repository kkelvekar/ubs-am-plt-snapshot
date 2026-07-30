using UBS.AM.PLT.Snapshot.Domain.Entities;

namespace UBS.AM.PLT.Snapshot.Domain;

/// <summary>
/// Outbound counterpart to <see cref="SnapshotMessage"/>: what this service tells the
/// publishing application about a snapshot's progress. Emitted today only when a snapshot
/// reaches COMPLETE; further statuses reuse the same shape.
///
/// Strongly typed on purpose — <see cref="Status"/> is the domain enum and the timestamps
/// are <see cref="DateTime"/>. Rendering them as the strings the org wire contract wants is
/// Infrastructure's job, so the transport format can change without touching the Domain.
/// </summary>
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
}
