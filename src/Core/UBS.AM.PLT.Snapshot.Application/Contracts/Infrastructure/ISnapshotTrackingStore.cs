using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;

namespace UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;

/// <summary>
/// Port for the snapshot_tracking table, which records per-snapshot completeness state.
/// </summary>
public interface ISnapshotTrackingStore
{
    /// <summary>
    /// Records a received payload — step 2 of the write order, called only after the blob
    /// write succeeded. The first payload for a snapshot INSERTs a RECEIVING row with
    /// <paramref name="adlsRootPath"/>; later payloads only set-union the payload's filename
    /// into received_files and advance last_updated_at, never touching adls_root_path or
    /// first_received_at, so redelivery is harmless. Returns the post-upsert row for the
    /// completeness check.
    /// </summary>
    Task<SnapshotTrackingEntity> UpsertReceivedAsync(SnapshotMessage message, string adlsRootPath, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the root path pinned by the snapshot's first payload, so subsequent and
    /// redelivered payloads reuse it instead of deriving a new one.
    /// Null means "not yet pinned, compute a fresh path": either no row exists, or the row is
    /// a rejection-only FAILED row (see <see cref="MarkRejectedAsync"/>) for which no blob was
    /// ever written. Callers need not distinguish the two.
    /// </summary>
    Task<string?> GetRootPathAsync(string snapshotId, CancellationToken cancellationToken);

    /// <summary>
    /// Flips a tracking row to COMPLETE, the final step of the write order. Called only after
    /// the index UPSERT succeeded (design §8) so that a failed index write leaves the row
    /// RECEIVING and redelivery retries the completeness check. Touches only status and
    /// completed_at.
    /// </summary>
    Task MarkCompleteAsync(string snapshotId, CancellationToken cancellationToken);

    /// <summary>
    /// Records a rejected message as a FAILED row — status, reason and declared_failed_at
    /// only. INSERTs a minimal FAILED row when the snapshot has none, so a snapshot whose
    /// first message is bad is still visible. Idempotent: repeating it only refreshes the
    /// reason and last_updated_at, and a COMPLETE row is left untouched.
    /// FAILED is recoverable — a later valid message flips the row back to RECEIVING via
    /// <see cref="UpsertReceivedAsync"/>. Returns the resulting row.
    /// </summary>
    Task<SnapshotTrackingEntity> MarkRejectedAsync(SnapshotRejectionRecord rejection, CancellationToken cancellationToken);
}
