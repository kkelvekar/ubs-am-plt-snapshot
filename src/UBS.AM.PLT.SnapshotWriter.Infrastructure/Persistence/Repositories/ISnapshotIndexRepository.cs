using UBS.AM.PLT.SnapshotWriter.Domain.Entities;

namespace UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence.Repositories;

/// <summary>
/// Raw EF Core data access for the snapshot_index table. Internal to Infrastructure —
/// this is not an Application port; the port stays <c>ISnapshotIndexStore</c>, and the
/// UPSERT/idempotency decisions stay in <see cref="SqlSnapshotIndexStore"/> (expressed as
/// the <c>apply</c> delegate). Same single-call contract as
/// <see cref="ISnapshotTrackingRepository"/>: one self-contained DbContext per call,
/// disposed before returning.
/// </summary>
internal interface ISnapshotIndexRepository
{
    /// <summary>
    /// Single-call read-modify-write: opens one DbContext, does a tracked SELECT by PK,
    /// invokes <paramref name="apply"/>, INSERTs when the row was absent, saves and
    /// disposes. <paramref name="apply"/> receives <c>null</c> when no row exists and
    /// must return the entity to persist; when a row exists it must mutate and return
    /// that same tracked instance. A failed save propagates so redelivery retries.
    /// </summary>
    Task UpsertAsync(
        string snapshotId,
        Func<SnapshotIndexEntity?, SnapshotIndexEntity> apply,
        CancellationToken cancellationToken);
}
