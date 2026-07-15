using UBS.AM.PLT.SnapshotWriter.Application.Contracts.Infrastructure;
using UBS.AM.PLT.SnapshotWriter.Domain.Entities;

namespace UBS.AM.PLT.SnapshotWriter.UnitTests.Fakes;

public sealed class FakeSnapshotIndexStore : ISnapshotIndexStore
{
    private readonly List<SnapshotIndexEntity> _upserts = [];

    /// <summary>
    /// Shared call-order log, injected by the test, so ordering against
    /// <see cref="FakeSnapshotTrackingStore.MarkCompleteAsync"/> can be asserted. Optional
    /// — tests that don't care about ordering can leave it null.
    /// </summary>
    public List<string>? CallOrderLog { get; set; }

    public IReadOnlyList<SnapshotIndexEntity> Upserts => _upserts;

    public Exception? ThrowOnUpsert { get; set; }

    public Task UpsertAsync(SnapshotIndexEntity entry, CancellationToken cancellationToken)
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
