using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Domain.Entities;

namespace UBS.AM.PLT.Snapshot.UnitTests.Fakes;

public sealed class FakePortfolioSnapshotIndexStore : IPortfolioSnapshotIndexStore
{
    private readonly List<PortfolioSnapshotIndexEntity> _upserts = [];

    /// <summary>
    /// Shared call-order log, injected by the test, so ordering against
    /// <see cref="FakeSnapshotTrackingStore.MarkCompleteAsync"/> can be asserted. Optional
    /// — tests that don't care about ordering can leave it null.
    /// </summary>
    public List<string>? CallOrderLog { get; set; }

    public IReadOnlyList<PortfolioSnapshotIndexEntity> Upserts => _upserts;

    public Exception? ThrowOnUpsert { get; set; }

    public Task UpsertAsync(PortfolioSnapshotIndexEntity entry, CancellationToken cancellationToken)
    {
        CallOrderLog?.Add(nameof(UpsertAsync));

        if (ThrowOnUpsert is not null)
        {
            throw ThrowOnUpsert;
        }

        _upserts.Add(entry);
        return Task.CompletedTask;
    }
}
