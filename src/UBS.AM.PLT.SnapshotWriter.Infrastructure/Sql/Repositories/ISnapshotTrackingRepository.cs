using UBS.AM.PLT.SnapshotWriter.Domain.Entities;

namespace UBS.AM.PLT.SnapshotWriter.Infrastructure.Sql.Repositories;

/// <summary>
/// Raw EF Core data access for the snapshot_tracking table. Internal to Infrastructure —
/// this is not an Application port; the port stays <c>ISnapshotTrackingStore</c>, and the
/// idempotency/upsert decisions stay in <see cref="SqlSnapshotTrackingStore"/> (expressed
/// as the <c>apply</c> delegate). Every method is a single self-contained call: it opens
/// its own DbContext and disposes it before returning, so the repository holds no state
/// between calls.
/// </summary>
internal interface ISnapshotTrackingRepository
{
    /// <summary>
    /// Single-call read-modify-write: opens one DbContext, does a tracked SELECT by PK,
    /// invokes <paramref name="apply"/>, INSERTs when the row was absent, saves and
    /// disposes. <paramref name="apply"/> receives <c>null</c> when no row exists and
    /// must return the entity to persist; when a row exists it must mutate and return
    /// that same tracked instance (returning an unchanged instance produces no SQL).
    /// A failed save (e.g. the rebalance-race PK violation) propagates so redelivery
    /// retries.
    /// </summary>
    Task<SnapshotTrackingEntity> UpsertAsync(
        string snapshotId,
        Func<SnapshotTrackingEntity?, SnapshotTrackingEntity> apply,
        CancellationToken cancellationToken);

    /// <summary>
    /// No-tracking projection of adls_root_path in its own context.
    /// </summary>
    Task<string?> FindRootPathAsync(string snapshotId, CancellationToken cancellationToken);
}
