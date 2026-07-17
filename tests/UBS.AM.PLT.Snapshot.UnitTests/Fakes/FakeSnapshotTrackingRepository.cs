using UBS.AM.PLT.Snapshot.Domain.Entities;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql.Repositories;

namespace UBS.AM.PLT.Snapshot.UnitTests.Fakes;

/// <summary>
/// In-memory <see cref="ISnapshotTrackingRepository"/>: <see cref="UpsertAsync"/> invokes
/// the store's <c>apply</c> delegate against a dictionary-held entity and stores the
/// result, so tests exercise the store's upsert/idempotency decisions without EF Core.
/// </summary>
internal sealed class FakeSnapshotTrackingRepository : ISnapshotTrackingRepository
{
    public Dictionary<string, SnapshotTrackingEntity> Rows { get; } = new(StringComparer.Ordinal);

    public Task<SnapshotTrackingEntity> UpsertAsync(
        string snapshotId,
        Func<SnapshotTrackingEntity?, SnapshotTrackingEntity> apply,
        CancellationToken cancellationToken)
    {
        Rows.TryGetValue(snapshotId, out var existing);
        var result = apply(existing);
        Rows[snapshotId] = result;
        return Task.FromResult(result);
    }

    public Task<string?> FindRootPathAsync(string snapshotId, CancellationToken cancellationToken)
        => Task.FromResult(Rows.TryGetValue(snapshotId, out var entry) ? entry.AdlsRootPath : null);
}
