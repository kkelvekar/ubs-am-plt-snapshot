namespace UBS.AM.PLT.Snapshot.Domain.Entities;

/// <summary>
/// Lifecycle of a snapshot_tracking row, per solution design §6. Persisted as the
/// strings RECEIVING / COMPLETE / FAILED (mapping owned by Infrastructure).
/// </summary>
public enum SnapshotTrackingStatus
{
    Receiving,
    Complete,
    Failed,
}
