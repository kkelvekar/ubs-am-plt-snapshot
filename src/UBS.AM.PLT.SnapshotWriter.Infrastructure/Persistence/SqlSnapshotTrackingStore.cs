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
/// Raw EF Core access lives in <see cref="ISnapshotTrackingRepository"/>; this class
/// keeps only the upsert/idempotency decisions, expressed as the <c>apply</c> delegate
/// each call passes to the repository's single-call upsert.
/// </summary>
internal sealed class SqlSnapshotTrackingStore : ISnapshotTrackingStore
{
    private readonly ISnapshotTrackingRepository _repository;
    private readonly TimeProvider _timeProvider;

    public SqlSnapshotTrackingStore(
        ISnapshotTrackingRepository repository,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _timeProvider = timeProvider;
    }

    public Task<SnapshotTrackingEntry> UpsertReceivedAsync(
        SnapshotMessage message,
        string adlsRootPath,
        CancellationToken cancellationToken)
    {
        var fileName = SnapshotBlobPath.FileName(message.PayloadType);
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        return _repository.UpsertAsync(
            message.SnapshotId,
            existing =>
            {
                if (existing is null)
                {
                    return new SnapshotTrackingEntry
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
                }

                if (!existing.ReceivedFiles.Contains(fileName, StringComparer.Ordinal))
                {
                    existing.ReceivedFiles = [.. existing.ReceivedFiles, fileName];
                }

                existing.LastUpdatedAt = now;
                return existing;
            },
            cancellationToken);
    }

    public Task<string?> GetRootPathAsync(string snapshotId, CancellationToken cancellationToken)
        => _repository.FindRootPathAsync(snapshotId, cancellationToken);

    public async Task MarkCompleteAsync(string snapshotId, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        await _repository.UpsertAsync(
            snapshotId,
            existing =>
            {
                if (existing is null)
                {
                    throw new InvalidOperationException(
                        $"Cannot mark snapshot '{snapshotId}' complete: no tracking row exists.");
                }

                if (existing.Status != SnapshotTrackingStatus.Complete)
                {
                    existing.Status = SnapshotTrackingStatus.Complete;
                    existing.CompletedAt = now;
                }

                // Already COMPLETE: returned unchanged, so SaveChanges detects no change
                // and issues no SQL — the §8 Scenario 5 redelivery no-op.
                return existing;
            },
            cancellationToken);
    }
}
