using UBS.AM.PLT.Snapshot.Domain.Entities;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql.Repositories;

namespace UBS.AM.PLT.Snapshot.UnitTests.Fakes;

/// <summary>
/// In-memory <see cref="IPortfolioSnapshotIndexRepository"/>: <see cref="UpsertAsync"/> invokes
/// the store's <c>apply</c> delegate against a dictionary-held entity and stores the
/// result, so tests exercise the store's UPSERT decisions without EF Core.
/// </summary>
internal sealed class FakePortfolioSnapshotIndexRepository : IPortfolioSnapshotIndexRepository
{
    public Dictionary<string, PortfolioSnapshotIndexEntity> Rows { get; } = new(StringComparer.Ordinal);

    public Task UpsertAsync(
        string snapshotId,
        Func<PortfolioSnapshotIndexEntity?, PortfolioSnapshotIndexEntity> apply,
        CancellationToken cancellationToken)
    {
        Rows.TryGetValue(snapshotId, out var existing);
        Rows[snapshotId] = apply(existing);
        return Task.CompletedTask;
    }
}
