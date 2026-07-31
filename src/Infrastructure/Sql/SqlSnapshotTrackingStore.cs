using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Application.Features.SnapshotIngestion;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql.Repositories;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Sql;

/// <summary>
/// SQL adapter for <see cref="ISnapshotTrackingStore"/>, using the read-then-write flow from
/// solution design §6: SELECT by primary key, then INSERT if absent or UPDATE if present. Raw
/// EF Core access lives in <see cref="ISnapshotTrackingRepository"/>; this class holds only
/// the upsert and idempotency decisions, expressed as the <c>apply</c> delegate each call
/// passes to the repository.
/// </summary>
/// <remarks>
/// No MERGE, no rowversion and no locking: the topic is partitioned by accountId, so all
/// messages for one snapshot are processed sequentially by a single consumer. The one
/// rebalance race, two consumers inserting the same snapshotId, surfaces as a primary key
/// violation that propagates, so redelivery retries through the UPDATE path.
/// </remarks>
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

    public Task<SnapshotTrackingEntity> UpsertReceivedAsync(
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
                    return new SnapshotTrackingEntity
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

                // Backfill only. A rejection-only row carries an empty path because no blob
                // was ever written for it, so the first real write pins it here. A non-empty
                // path is never overwritten, whatever the status.
                if (string.IsNullOrEmpty(existing.AdlsRootPath))
                {
                    existing.AdlsRootPath = adlsRootPath;
                }

                // FAILED is recoverable: a valid message means files are still arriving, so
                // the row returns to RECEIVING with the rejection cleared. COMPLETE is not
                // touched — it is terminal and a redelivery must never regress it (§8).
                if (existing.Status == SnapshotTrackingStatus.Failed)
                {
                    existing.Status = SnapshotTrackingStatus.Receiving;
                    existing.Reason = null;
                    existing.DeclaredFailedAt = null;
                }

                existing.LastUpdatedAt = now;
                return existing;
            },
            cancellationToken);
    }

    public async Task<string?> GetRootPathAsync(string snapshotId, CancellationToken cancellationToken)
    {
        // A rejection-only FAILED row carries an empty path, which is the same "not yet
        // pinned" state as no row at all and must be reported as null. An empty string would
        // survive the caller's ?? and land every blob at the container root.
        var rootPath = await _repository.FindRootPathAsync(snapshotId, cancellationToken);
        return string.IsNullOrEmpty(rootPath) ? null : rootPath;
    }

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

                // Already COMPLETE: returned unchanged, so SaveChanges issues no SQL.
                return existing;
            },
            cancellationToken);
    }

    public Task<SnapshotTrackingEntity> MarkRejectedAsync(
        SnapshotRejectionRecord rejection,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var reason = $"{rejection.ReasonCode}: {rejection.ReasonDetail}";

        return _repository.UpsertAsync(
            rejection.SnapshotId,
            existing =>
            {
                if (existing is null)
                {
                    // The snapshot's first message was the bad one. A minimal FAILED row is
                    // still inserted so the rejection is visible in SQL and not only in the
                    // log; no file was written, hence no root path and no received files.
                    return new SnapshotTrackingEntity
                    {
                        SnapshotId = rejection.SnapshotId,
                        AccountId = Storable(rejection.AccountId, SnapshotFieldLimits.AccountIdMaxLength),
                        SnapshotType = Storable(rejection.SnapshotType, SnapshotFieldLimits.SnapshotTypeMaxLength),
                        AdlsRootPath = string.Empty,
                        ReceivedFiles = [],
                        Status = SnapshotTrackingStatus.Failed,
                        Reason = reason,
                        FirstReceivedAt = now,
                        LastUpdatedAt = now,
                        DeclaredFailedAt = now,
                    };
                }

                // COMPLETE is terminal: a late bad message says nothing about the index row
                // already written, so the row is returned untouched and no SQL is issued.
                if (existing.Status == SnapshotTrackingStatus.Complete)
                {
                    return existing;
                }

                // received_files, adls_root_path and first_received_at are never touched here,
                // so whatever already arrived stays recorded and a later valid message can
                // recover the snapshot (see UpsertReceivedAsync).
                existing.Status = SnapshotTrackingStatus.Failed;
                existing.Reason = reason;
                existing.DeclaredFailedAt ??= now;
                existing.LastUpdatedAt = now;
                return existing;
            },
            cancellationToken);
    }

    /// <summary>
    /// Normalises a descriptive field that may itself be the reason the message was rejected
    /// (null, or too long for its NOT NULL column) to an empty string, so recording the
    /// rejection cannot fail on the value that caused it. The raw values still reach the
    /// publishing application in the response.
    /// </summary>
    private static string Storable(string? value, int maxLength)
        => value is not null && value.Length <= maxLength ? value : string.Empty;
}
