using UBS.AM.PLT.SnapshotWriter.Domain.Entities;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Sql.Repositories;

namespace UBS.AM.PLT.SnapshotWriter.UnitTests.Fakes;

/// <summary>
/// In-memory <see cref="ISnapshotIndexRepository"/>: <see cref="UpsertAsync"/> invokes
/// the store's <c>apply</c> delegate against a dictionary-held entity and stores the
/// result, so tests exercise the store's UPSERT decisions without EF Core.
/// </summary>
internal sealed class FakeSnapshotIndexRepository : ISnapshotIndexRepository
{
    public Dictionary<string, SnapshotIndexEntity> Rows { get; } = new(StringComparer.Ordinal);

    public Task UpsertAsync(
        string snapshotId,
        Func<SnapshotIndexEntity?, SnapshotIndexEntity> apply,
        CancellationToken cancellationToken)
    {
        Rows.TryGetValue(snapshotId, out var existing);
        Rows[snapshotId] = apply(existing);
        return Task.CompletedTask;
    }
}
