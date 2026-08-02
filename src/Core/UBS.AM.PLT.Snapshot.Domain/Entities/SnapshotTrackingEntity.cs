namespace UBS.AM.PLT.Snapshot.Domain.Entities;

/// <summary>
/// One row of the snapshot_tracking table (solution design §6): transient per-snapshot
/// completeness state with a 30-day rolling retention. Plain POCO; the persistence mapping
/// lives in Infrastructure.
/// </summary>
/// <remarks>
/// <see cref="MissingFiles"/> and <see cref="Alerted"/> are written only by the daily cleanup
/// job, which is out of scope for this repository. <see cref="DeclaredFailedAt"/> and
/// <see cref="Reason"/> are written by that job and by the write path when a message is
/// rejected, and are cleared again if a later valid message recovers the snapshot.
/// </remarks>
public class SnapshotTrackingEntity
{
    public required string SnapshotId { get; set; }
    public required string AccountId { get; set; }
    public required string SnapshotType { get; set; }
    public required string AdlsRootPath { get; set; }

    /// <summary>Received filenames (e.g. "orders.json"), built via <see cref="SnapshotBlobPath.FileName"/>.</summary>
    public List<string> ReceivedFiles { get; set; } = [];

    /// <summary>Populated only when <see cref="Status"/> is <see cref="SnapshotTrackingStatus.Failed"/>.</summary>
    public List<string>? MissingFiles { get; set; }

    public SnapshotTrackingStatus Status { get; set; }

    /// <summary>
    /// Why the snapshot is <see cref="SnapshotTrackingStatus.Failed"/>, as
    /// <c>"{reasonCode}: {detail}"</c>. Null while the row is RECEIVING or COMPLETE.
    /// </summary>
    public string? Reason { get; set; }

    public DateTime FirstReceivedAt { get; set; }
    public DateTime LastUpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? DeclaredFailedAt { get; set; }
    public bool Alerted { get; set; }
}
