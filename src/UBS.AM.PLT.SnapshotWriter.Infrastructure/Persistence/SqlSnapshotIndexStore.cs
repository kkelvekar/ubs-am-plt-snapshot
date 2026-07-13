using Microsoft.EntityFrameworkCore;
using UBS.AM.PLT.SnapshotWriter.Application.Interfaces.Infrastructure;
using UBS.AM.PLT.SnapshotWriter.Domain;

namespace UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence;

/// <summary>
/// SQL adapter for <see cref="ISnapshotIndexStore"/> using the same read-then-write flow
/// as <see cref="SqlSnapshotTrackingStore"/> (SELECT by PK; INSERT if absent, else
/// UPDATE) — design §7, §8. created_at is set only on first INSERT and never touched on
/// UPDATE, so redelivery of the completing message upserts identical row content without
/// disturbing the original write timestamp.
/// </summary>
public sealed class SqlSnapshotIndexStore : ISnapshotIndexStore
{
    private readonly IDbContextFactory<SnapshotWriterDbContext> _contextFactory;
    private readonly TimeProvider _timeProvider;

    public SqlSnapshotIndexStore(
        IDbContextFactory<SnapshotWriterDbContext> contextFactory,
        TimeProvider timeProvider)
    {
        _contextFactory = contextFactory;
        _timeProvider = timeProvider;
    }

    public async Task UpsertAsync(SnapshotIndexEntry entry, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await context.SnapshotIndex
            .SingleOrDefaultAsync(e => e.SnapshotId == entry.SnapshotId, cancellationToken);

        if (existing is null)
        {
            entry.CreatedAt = _timeProvider.GetUtcNow().UtcDateTime;
            context.SnapshotIndex.Add(entry);
        }
        else
        {
            existing.AccountId = entry.AccountId;
            existing.SnapshotDate = entry.SnapshotDate;
            existing.EventType = entry.EventType;
            existing.AdlsPath = entry.AdlsPath;
            existing.DisplayData = entry.DisplayData;
            // created_at is intentionally left untouched on UPDATE.
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
