using System.Text.Json;
using Microsoft.Extensions.Logging;
using UBS.AM.PLT.SnapshotWriter.Application.Contracts;
using UBS.AM.PLT.SnapshotWriter.Application.Contracts.Infrastructure;
using UBS.AM.PLT.SnapshotWriter.Application.Models;
using UBS.AM.PLT.SnapshotWriter.Domain;
using UBS.AM.PLT.SnapshotWriter.Domain.Entities;

namespace UBS.AM.PLT.SnapshotWriter.Application;

/// <summary>
/// Orchestrates the strict write order per message:
/// blob write → tracking upsert → completeness check → index UPSERT. Any failure
/// propagates unchanged so the consumer never commits the offset (recovery is forward,
/// via redelivery).
/// </summary>
public sealed class SnapshotMessageHandler : ISnapshotMessageHandler
{
    private static readonly JsonSerializerOptions HeaderSerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ISnapshotBlobStore _blobStore;
    private readonly ISnapshotTrackingStore _trackingStore;
    private readonly IRequiredFilesProvider _requiredFilesProvider;
    private readonly ISnapshotIndexStore _indexStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SnapshotMessageHandler> _logger;

    public SnapshotMessageHandler(
        ISnapshotBlobStore blobStore,
        ISnapshotTrackingStore trackingStore,
        IRequiredFilesProvider requiredFilesProvider,
        ISnapshotIndexStore indexStore,
        TimeProvider timeProvider,
        ILogger<SnapshotMessageHandler> logger)
    {
        _blobStore = blobStore;
        _trackingStore = trackingStore;
        _requiredFilesProvider = requiredFilesProvider;
        _indexStore = indexStore;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task HandleAsync(SnapshotMessage message, CancellationToken cancellationToken)
    {
        // Guard: the JSON `required` check on the envelope only proves each identity field
        // was present in the message — not that it was non-null (an explicit
        // "snapshotId": null passes deserialisation with a null value). A null identity
        // field would corrupt the blob path (e.g. ".../snapshotId=/...") and the tracking
        // row, so reject it before the first write. Throwing routes it through the
        // consumer's existing no-commit / seek-back / retry / alert path, identical to a
        // malformed envelope — recovery is forward, never a silent skip.
        ValidateIdentity(message);

        // The root folder is pinned to the FIRST payload's arrival time for this
        // snapshotId and reused by every subsequent (or redelivered) payload, so a
        // snapshot whose publish timestamps straddle a month/year boundary never splits
        // across two folders. The lookup is read-only (mutates nothing) — the write order
        // below still starts at the blob write. Kafka's accountId partitioning processes
        // a snapshot's messages sequentially on one consumer (see SqlSnapshotTrackingStore),
        // so no locking is needed around it.
        var rootPath = await _trackingStore.GetRootPathAsync(message.SnapshotId, cancellationToken)
            ?? SnapshotBlobPath.RootFolder(message, _timeProvider.GetUtcNow());
        await _blobStore.WriteAsync(message, rootPath, cancellationToken);
        var tracking = await _trackingStore.UpsertReceivedAsync(message, rootPath, cancellationToken);

        // Guard: stray redelivery of a file long after the snapshot already completed (or
        // failed) must do nothing beyond steps 1-2 above — no re-fetch, re-upsert or
        // re-touch of a row that is no longer RECEIVING.
        if (tracking.Status == SnapshotTrackingStatus.Receiving)
        {
            var required = _requiredFilesProvider.GetRequiredFiles(message.SnapshotType);
            if (SnapshotCompleteness.IsComplete(tracking.ReceivedFiles, required))
            {
                var headerJson = await _blobStore.ReadHeaderAsync(tracking.AdlsRootPath, cancellationToken);
                var header = JsonSerializer.Deserialize<HeaderPayload>(headerJson, HeaderSerializerOptions)
                    ?? throw new JsonException("header.json deserialised to null.");

                var indexEntry = BuildIndexEntry(message, tracking, header);

                // Index UPSERT must precede the status flip: if the index write fails,
                // tracking must still read RECEIVING on redelivery so this guard retries.
                await _indexStore.UpsertAsync(indexEntry, cancellationToken);
                await _trackingStore.MarkCompleteAsync(message.SnapshotId, cancellationToken);
            }
        }

        _logger.LogInformation(
            "Wrote snapshot payload blob and upserted tracking snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType} stage={Stage} rootPath={RootPath} receivedFileCount={ReceivedFileCount}",
            message.SnapshotId,
            message.AccountId,
            message.PayloadType,
            message.Stage,
            rootPath,
            tracking.ReceivedFiles.Count);
    }

    private static void ValidateIdentity(SnapshotMessage message)
    {
        // Only the fields that form the blob path and tracking identity are checked —
        // a null in any of them corrupts a write. Deserialisation guarantees presence;
        // this guards against present-but-null. (Non-nullable reference-type annotations
        // are not enforced at runtime, so this check is real, not redundant.)
        ThrowIfNull(message.SnapshotId, nameof(message.SnapshotId));
        ThrowIfNull(message.AccountId, nameof(message.AccountId));
        ThrowIfNull(message.SnapshotType, nameof(message.SnapshotType));
        ThrowIfNull(message.PayloadType, nameof(message.PayloadType));

        static void ThrowIfNull(string? value, string fieldName)
        {
            if (value is null)
            {
                throw new ArgumentException(
                    $"Snapshot message envelope has a null required field '{fieldName}'; rejecting before any write.",
                    nameof(message));
            }
        }
    }

    private static SnapshotIndexEntity BuildIndexEntry(
        SnapshotMessage message,
        SnapshotTrackingEntity tracking,
        HeaderPayload header)
        => new()
        {
            SnapshotId = message.SnapshotId,
            AccountId = message.AccountId,
            SnapshotDate = tracking.FirstReceivedAt,
            EventType = header.EventType,
            AdlsPath = tracking.AdlsRootPath,
            DisplayData = new SnapshotIndexDisplayData
            {
                Benchmark = header.Benchmark,
                BaseCcy = header.BaseCcy,
                ProgramId = header.ProgramId,
                BatchId = header.BatchId,
                NumOrders = header.NumOrders,
                PtcAlerts = header.PtcAlerts,
                OrderApprovedBy = header.OrderApprovedBy,
                OrderApprovedAt = header.OrderApprovedAt,
                OrderSentBy = header.OrderSentBy,
                OrderSentAt = header.OrderSentAt,
            },
        };
}
