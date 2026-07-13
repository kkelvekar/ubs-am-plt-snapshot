using UBS.AM.PLT.SnapshotWriter.Domain;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence;

namespace UBS.AM.PLT.SnapshotWriter.UnitTests.Fakes;

/// <summary>
/// In-memory <see cref="ISnapshotIndexRepository"/>: <see cref="UpsertAsync"/> invokes
/// the store's <c>apply</c> delegate against a dictionary-held entity and stores the
/// result, so tests exercise the store's UPSERT decisions without EF Core.
/// </summary>
internal sealed class FakeSnapshotIndexRepository : ISnapshotIndexRepository
{
    public Dictionary<string, SnapshotIndexEntry> Rows { get; } = new(StringComparer.Ordinal);

    public Task UpsertAsync(
        string snapshotId,
        Func<SnapshotIndexEntry?, SnapshotIndexEntry> apply,
        CancellationToken cancellationToken)
    {
        Rows.TryGetValue(snapshotId, out var existing);
        Rows[snapshotId] = apply(existing);
        return Task.CompletedTask;
    }
}
