using UBS.AM.PLT.SnapshotWriter.Application.Interfaces.Infrastructure;
using UBS.AM.PLT.SnapshotWriter.Domain;

namespace UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence;

/// <summary>
/// SQL adapter for <see cref="ISnapshotIndexStore"/> using the same read-then-write flow
/// as <see cref="SqlSnapshotTrackingStore"/> (SELECT by PK; INSERT if absent, else
/// UPDATE) — design §7, §8. created_at is set only on first INSERT and never touched on
/// UPDATE, so redelivery of the completing message upserts identical row content without
/// disturbing the original write timestamp.
/// Raw EF Core access lives in <see cref="ISnapshotIndexRepository"/>; this class keeps
/// only the UPSERT/idempotency decisions, expressed as the <c>apply</c> delegate passed
/// to the repository's single-call upsert.
/// </summary>
internal sealed class SqlSnapshotIndexStore : ISnapshotIndexStore
{
    private readonly ISnapshotIndexRepository _repository;
    private readonly TimeProvider _timeProvider;

    public SqlSnapshotIndexStore(
        ISnapshotIndexRepository repository,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _timeProvider = timeProvider;
    }

    public Task UpsertAsync(SnapshotIndexEntry entry, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        return _repository.UpsertAsync(
            entry.SnapshotId,
            existing =>
            {
                if (existing is null)
                {
                    entry.CreatedAt = now;
                    return entry;
                }

                existing.AccountId = entry.AccountId;
                existing.SnapshotDate = entry.SnapshotDate;
                existing.EventType = entry.EventType;
                existing.AdlsPath = entry.AdlsPath;
                existing.DisplayData = entry.DisplayData;
                // created_at is intentionally left untouched on UPDATE.
                return existing;
            },
            cancellationToken);
    }
}
