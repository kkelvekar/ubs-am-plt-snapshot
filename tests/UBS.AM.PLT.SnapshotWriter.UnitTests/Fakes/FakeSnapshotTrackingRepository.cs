using UBS.AM.PLT.SnapshotWriter.Domain;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence;

namespace UBS.AM.PLT.SnapshotWriter.UnitTests.Fakes;

/// <summary>
/// In-memory <see cref="ISnapshotTrackingRepository"/>: <see cref="UpsertAsync"/> invokes
/// the store's <c>apply</c> delegate against a dictionary-held entity and stores the
/// result, so tests exercise the store's upsert/idempotency decisions without EF Core.
/// </summary>
internal sealed class FakeSnapshotTrackingRepository : ISnapshotTrackingRepository
{
    public Dictionary<string, SnapshotTrackingEntry> Rows { get; } = new(StringComparer.Ordinal);

    public Task<SnapshotTrackingEntry> UpsertAsync(
        string snapshotId,
        Func<SnapshotTrackingEntry?, SnapshotTrackingEntry> apply,
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
