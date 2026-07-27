namespace UBS.AM.PLT.Snapshot.Domain.Entities;

/// <summary>
/// One row of the snapshot_tracking table, per solution design §6: transient
/// per-snapshot completeness state with a 30-day rolling retention. Plain POCO —
/// persistence mapping lives in Infrastructure. <see cref="MissingFiles"/>,
/// <see cref="DeclaredFailedAt"/> and <see cref="Alerted"/> are written only by the
/// daily cleanup job, which is out of scope for this repository.
/// </summary>
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
    public DateTime FirstReceivedAt { get; set; }
    public DateTime LastUpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? DeclaredFailedAt { get; set; }
    public bool Alerted { get; set; }
}
