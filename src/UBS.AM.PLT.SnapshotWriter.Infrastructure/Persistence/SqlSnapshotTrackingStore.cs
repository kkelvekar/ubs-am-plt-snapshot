using Microsoft.EntityFrameworkCore;
using UBS.AM.PLT.SnapshotWriter.Application.Interfaces.Infrastructure;
using UBS.AM.PLT.SnapshotWriter.Domain;

namespace UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence;

/// <summary>
/// SQL adapter for <see cref="ISnapshotTrackingStore"/> using the read-then-write flow
/// from solution design §6 (SELECT by PK; INSERT if absent, else UPDATE). No MERGE, no
/// rowversion, no locks: the topic is partitioned by accountId, so all messages of a
/// snapshot are processed sequentially by one consumer. The one rebalance race — two
/// consumers inserting the same snapshotId — surfaces as a PK violation
/// (DbUpdateException) that propagates so redelivery retries via the UPDATE path.
/// The update path only set-unions the filename and advances last_updated_at; it never
/// touches status, adls_root_path or first_received_at, so a COMPLETE row stays
/// COMPLETE under redelivery.
/// </summary>
public sealed class SqlSnapshotTrackingStore : ISnapshotTrackingStore
{
    private readonly IDbContextFactory<SnapshotWriterDbContext> _contextFactory;
    private readonly TimeProvider _timeProvider;

    public SqlSnapshotTrackingStore(
        IDbContextFactory<SnapshotWriterDbContext> contextFactory,
        TimeProvider timeProvider)
    {
        _contextFactory = contextFactory;
        _timeProvider = timeProvider;
    }

    public async Task<SnapshotTrackingEntry> UpsertReceivedAsync(
        SnapshotMessage message,
        string adlsRootPath,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var fileName = SnapshotBlobPath.FileName(message.PayloadType);
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var entry = await context.SnapshotTracking
            .SingleOrDefaultAsync(e => e.SnapshotId == message.SnapshotId, cancellationToken);

        if (entry is null)
        {
            entry = new SnapshotTrackingEntry
            {
                SnapshotId = message.SnapshotId,
                AccountId = message.AccountId,
                SnapshotType = message.SnapshotType,
                AdlsRootPath = adlsRootPath,
                ReceivedFiles = [fileName],
                Status = SnapshotTrackingStatus.Receiving,
                FirstReceivedAt = now,
                LastUpdatedAt = now,
            };
            context.SnapshotTracking.Add(entry);
        }
        else
        {
            if (!entry.ReceivedFiles.Contains(fileName, StringComparer.Ordinal))
            {
                entry.ReceivedFiles = [.. entry.ReceivedFiles, fileName];
            }

            entry.LastUpdatedAt = now;
        }

        await context.SaveChangesAsync(cancellationToken);

        return entry;
    }

    public async Task MarkCompleteAsync(string snapshotId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var entry = await context.SnapshotTracking
            .SingleOrDefaultAsync(e => e.SnapshotId == snapshotId, cancellationToken);

        if (entry is null)
        {
            throw new InvalidOperationException(
                $"Cannot mark snapshot '{snapshotId}' complete: no tracking row exists.");
        }

        if (entry.Status != SnapshotTrackingStatus.Complete)
        {
            entry.Status = SnapshotTrackingStatus.Complete;
            entry.CompletedAt = _timeProvider.GetUtcNow().UtcDateTime;

            await context.SaveChangesAsync(cancellationToken);
        }
    }
}
