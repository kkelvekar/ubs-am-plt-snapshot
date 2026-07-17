using UBS.AM.PLT.Snapshot.Domain.Entities;

namespace UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;

/// <summary>
/// Port for the index UPSERT — step 4 of the strict write order, called only once the
/// snapshot is complete and only before the tracking row is marked COMPLETE (design §7,
/// §8). The write is idempotent: redelivery upserts the same row content.
/// </summary>
public interface ISnapshotIndexStore
{
    Task UpsertAsync(SnapshotIndexEntity entry, CancellationToken cancellationToken);
}
