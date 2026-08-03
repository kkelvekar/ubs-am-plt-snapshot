using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;

namespace UBS.AM.PLT.Snapshot.UnitTests.Fakes;

public sealed class FakeSnapshotTrackingStore : ISnapshotTrackingStore
{
    private readonly List<(SnapshotMessage Message, string AdlsRootPath)> _upserts = [];
    private readonly List<string> _markedComplete = [];
    private readonly List<SnapshotRejectionRecord> _markedRejected = [];

    public IReadOnlyList<(SnapshotMessage Message, string AdlsRootPath)> Upserts => _upserts;

    public IReadOnlyList<string> MarkedComplete => _markedComplete;

    public IReadOnlyList<SnapshotRejectionRecord> MarkedRejected => _markedRejected;

    public Exception? ThrowOnUpsert { get; set; }

    public Exception? ThrowOnMarkComplete { get; set; }

    public Exception? ThrowOnMarkRejected { get; set; }

    /// <summary>Status (and received files) returned by the next <see cref="UpsertReceivedAsync"/> call.</summary>
    public SnapshotTrackingStatus StatusToReturn { get; set; } = SnapshotTrackingStatus.Receiving;

    /// <summary>
    /// Pinned root path per snapshotId, mirroring the real store: the first
    /// <see cref="UpsertReceivedAsync"/> pins it, later upserts never change it, and
    /// <see cref="GetRootPathAsync"/> reads it (null when nothing pinned yet). Tests can
    /// pre-seed an entry to simulate an existing tracking row.
    /// </summary>
    public Dictionary<string, string> RootPathsBySnapshotId { get; } = new(StringComparer.Ordinal);

    public List<string> ReceivedFilesToReturn { get; set; } = [];

    /// <summary>
    /// Shared call-order log, injected by the test, so ordering of
    /// <see cref="MarkCompleteAsync"/> against <see cref="FakePortfolioSnapshotIndexStore.UpsertAsync"/>
    /// can be asserted. Optional — tests that don't care about ordering can leave it null.
    /// </summary>
    public List<string>? CallOrderLog { get; set; }

    public Task<SnapshotTrackingEntity> UpsertReceivedAsync(
        SnapshotMessage message,
        string adlsRootPath,
        CancellationToken cancellationToken)
    {
        if (ThrowOnUpsert is not null)
        {
            throw ThrowOnUpsert;
        }

        _upserts.Add((message, adlsRootPath));
        RootPathsBySnapshotId.TryAdd(message.SnapshotId, adlsRootPath);

        var receivedFiles = ReceivedFilesToReturn.Count > 0
            ? ReceivedFilesToReturn
            : [SnapshotBlobPath.FileName(message.PayloadType)];

        return Task.FromResult(new SnapshotTrackingEntity
        {
            SnapshotId = message.SnapshotId,
            AccountId = message.AccountId,
            SnapshotType = message.SnapshotType,
            AdlsRootPath = adlsRootPath,
            ReceivedFiles = receivedFiles,
            Status = StatusToReturn,
            FirstReceivedAt = new DateTime(2026, 5, 22, 6, 10, 14, DateTimeKind.Utc),
            LastUpdatedAt = new DateTime(2026, 5, 22, 6, 10, 14, DateTimeKind.Utc),
        });
    }

    public Task<string?> GetRootPathAsync(string snapshotId, CancellationToken cancellationToken)
        => Task.FromResult(RootPathsBySnapshotId.TryGetValue(snapshotId, out var rootPath) ? rootPath : null);

    public Task MarkCompleteAsync(string snapshotId, CancellationToken cancellationToken)
    {
        CallOrderLog?.Add(nameof(MarkCompleteAsync));

        if (ThrowOnMarkComplete is not null)
        {
            throw ThrowOnMarkComplete;
        }

        _markedComplete.Add(snapshotId);
        return Task.CompletedTask;
    }

    /// <summary>Row returned by <see cref="MarkRejectedAsync"/>; a minimal FAILED row unless a test sets one.</summary>
    public SnapshotTrackingEntity? RejectedRowToReturn { get; set; }

    public Task<SnapshotTrackingEntity> MarkRejectedAsync(
        SnapshotRejectionRecord rejection,
        CancellationToken cancellationToken)
    {
        CallOrderLog?.Add(nameof(MarkRejectedAsync));

        if (ThrowOnMarkRejected is not null)
        {
            throw ThrowOnMarkRejected;
        }

        _markedRejected.Add(rejection);

        return Task.FromResult(RejectedRowToReturn ?? new SnapshotTrackingEntity
        {
            SnapshotId = rejection.SnapshotId,
            AccountId = rejection.AccountId ?? string.Empty,
            SnapshotType = rejection.SnapshotType ?? string.Empty,
            AdlsRootPath = string.Empty,
            ReceivedFiles = [],
            Status = SnapshotTrackingStatus.Failed,
            Reason = $"{rejection.ReasonCode}: {rejection.ReasonDetail}",
            FirstReceivedAt = new DateTime(2026, 5, 22, 6, 10, 14, DateTimeKind.Utc),
            LastUpdatedAt = new DateTime(2026, 5, 22, 6, 10, 14, DateTimeKind.Utc),
            DeclaredFailedAt = new DateTime(2026, 5, 22, 6, 10, 14, DateTimeKind.Utc),
        });
    }
}
