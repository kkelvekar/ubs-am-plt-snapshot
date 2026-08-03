using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Domain.Entities;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql.Repositories;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Sql;

/// <summary>
/// SQL adapter for <see cref="IPortfolioSnapshotIndexStore"/> using the same read-then-write flow
/// as <see cref="SqlSnapshotTrackingStore"/> (SELECT by PK; INSERT if absent, else
/// UPDATE) — design §7, §8. created_at is set only on first INSERT and never touched on
/// UPDATE, so redelivery of the completing message upserts identical row content without
/// disturbing the original write timestamp.
/// Raw EF Core access lives in <see cref="IPortfolioSnapshotIndexRepository"/>; this class keeps
/// only the UPSERT/idempotency decisions, expressed as the <c>apply</c> delegate passed
/// to the repository's single-call upsert.
/// </summary>
internal sealed class SqlPortfolioSnapshotIndexStore : IPortfolioSnapshotIndexStore
{
    private readonly IPortfolioSnapshotIndexRepository _repository;
    private readonly TimeProvider _timeProvider;

    public SqlPortfolioSnapshotIndexStore(
        IPortfolioSnapshotIndexRepository repository,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _timeProvider = timeProvider;
    }

    public Task UpsertAsync(PortfolioSnapshotIndexEntity entry, CancellationToken cancellationToken)
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
