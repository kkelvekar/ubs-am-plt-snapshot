using Microsoft.Extensions.Logging;
using UBS.AM.PLT.Snapshot.Application.Contracts;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;

namespace UBS.AM.PLT.Snapshot.Application.Features.SnapshotIngestion;

/// <summary>
/// Orchestrates the strict write order per message:
/// blob write → tracking upsert → completeness check → index UPSERT. Any failure
/// propagates unchanged so the consumer never commits the offset (recovery is forward,
/// via redelivery).
/// </summary>
public sealed class SnapshotMessageHandler : ISnapshotMessageHandler
{
    private readonly ISnapshotBlobStore _blobStore;
    private readonly ISnapshotTrackingStore _trackingStore;
    private readonly IRequiredFilesProvider _requiredFilesProvider;
    private readonly ISnapshotIndexStore _indexStore;
    private readonly ISnapshotResponsePublisher _responsePublisher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SnapshotMessageHandler> _logger;

    public SnapshotMessageHandler(
        ISnapshotBlobStore blobStore,
        ISnapshotTrackingStore trackingStore,
        IRequiredFilesProvider requiredFilesProvider,
        ISnapshotIndexStore indexStore,
        ISnapshotResponsePublisher responsePublisher,
        TimeProvider timeProvider,
        ILogger<SnapshotMessageHandler> logger)
    {
        _blobStore = blobStore;
        _trackingStore = trackingStore;
        _requiredFilesProvider = requiredFilesProvider;
        _indexStore = indexStore;
        _responsePublisher = responsePublisher;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task HandleAsync(SnapshotMessage message, CancellationToken cancellationToken)
    {
        // Guard: the JSON `required` check on the envelope only proves each identity field
        // was present in the message — not that it was non-null (an explicit
        // "snapshotId": null passes deserialisation with a null value). A null identity
        // field would corrupt the blob path (e.g. ".../snapshotId=/...") and the tracking
        // row, so reject it before the first write. This throws
        // SnapshotMessageRejectedException rather than a generic failure: retrying the same
        // bytes can never succeed, so the consumer commits past it instead of seeking back
        // and blocking the partition forever.
        SnapshotEnvelopeValidator.ValidateEnvelope(message);
        SnapshotEnvelopeValidator.ValidatePayloadType(message, _requiredFilesProvider);

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
                var eventType = SnapshotIndexEntryBuilder.ExtractEventType(headerJson);
                if (eventType.Length == 0)
                {
                    _logger.LogWarning(
                        "Header has no usable eventType; writing the index row with an empty EventType snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType}",
                        message.SnapshotId,
                        message.AccountId,
                        message.PayloadType);
                }

                var indexEntry = SnapshotIndexEntryBuilder.Build(message, tracking, headerJson, eventType);

                // Index UPSERT must precede the status flip: if the index write fails,
                // tracking must still read RECEIVING on redelivery so this guard retries.
                await _indexStore.UpsertAsync(indexEntry, cancellationToken);

                // Publish must ALSO precede the status flip, for the same reason as the
                // index UPSERT: once the row reads COMPLETE this guard stops firing, so a
                // notification that failed after the flip could never be retried — the
                // redelivery would sail through and commit the offset having told the
                // publisher nothing. Publishing first makes the notification at-least-once
                // (a failure between publish and flip re-publishes on redelivery) instead
                // of silently at-most-once.
                await PublishCompletedAsync(message, tracking);

                await _trackingStore.MarkCompleteAsync(message.SnapshotId, cancellationToken);

                // Distinct completion event: the business-critical moment the snapshot
                // becomes visible in the audit UI. Answers "when did snapshot X complete".
                _logger.LogInformation(
                    "Snapshot complete: index row upserted and tracking marked complete snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType} adlsRootPath={AdlsRootPath} eventType={EventType}",
                    message.SnapshotId,
                    message.AccountId,
                    message.PayloadType,
                    tracking.AdlsRootPath,
                    eventType);
            }
            else if (tracking.ReceivedFiles.Count == 1)
            {
                await PublishReceivingAsync(message, tracking, required);
            }
        }

        _logger.LogInformation(
            "Wrote snapshot payload blob and upserted tracking snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType} rootPath={RootPath} receivedFileCount={ReceivedFileCount}",
            message.SnapshotId,
            message.AccountId,
            message.PayloadType,
            rootPath,
            tracking.ReceivedFiles.Count);
    }

    /// <summary>
    /// Announces that a new snapshot has started arriving: emitted once, on the payload that
    /// creates the tracking row, so the publisher learns the snapshot is being received and
    /// which files are still outstanding without waiting for completion.
    /// </summary>
    /// <remarks>
    /// "First" is keyed off the received-file count, not off "no tracking row existed before
    /// this message". The count is stable under redelivery of that same first payload (the
    /// upsert set-unions the filename), so a failed publish is retried on redelivery instead
    /// of being lost the moment the row exists. The completing payload publishes
    /// <c>Complete</c> instead, never both — so a snapshot type requiring a single file emits
    /// one response, not two.
    /// </remarks>
    private async Task PublishReceivingAsync(
        SnapshotMessage message,
        SnapshotTrackingEntity tracking,
        IReadOnlySet<string> requiredFiles)
    {
        var missingFiles = MissingFiles(tracking.ReceivedFiles, requiredFiles);

        await _responsePublisher.PublishAsync(new SnapshotStatusNotification
        {
            SnapshotId = tracking.SnapshotId,
            AccountId = tracking.AccountId,
            ReceivedFiles = tracking.ReceivedFiles,
            MissingFiles = missingFiles,
            Status = SnapshotTrackingStatus.Receiving,
            FirstReceivedAt = tracking.FirstReceivedAt,
            LastUpdatedAt = tracking.LastUpdatedAt,
        });

        _logger.LogInformation(
            "Published snapshot receiving response snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType} missingFileCount={MissingFileCount}",
            message.SnapshotId,
            message.AccountId,
            message.PayloadType,
            missingFiles.Count);
    }

    /// <summary>
    /// Required files not yet received, sorted so the same snapshot state always produces the
    /// same wire value (the required-file set has no inherent order).
    /// </summary>
    private static IReadOnlyList<string> MissingFiles(
        IReadOnlyCollection<string> receivedFiles,
        IReadOnlySet<string> requiredFiles)
        => [.. requiredFiles.Except(receivedFiles, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>
    /// Tells the publishing application the snapshot is done. <c>CompletedAt</c> is stamped
    /// from the same <see cref="TimeProvider"/> the tracking store flips the row with a
    /// moment later, so the notification and the persisted <c>completed_at</c> agree to the
    /// resolution anyone cares about. <c>MissingFiles</c> is empty by definition here.
    /// </summary>
    private async Task PublishCompletedAsync(SnapshotMessage message, SnapshotTrackingEntity tracking)
    {
        var completedAt = _timeProvider.GetUtcNow().UtcDateTime;

        await _responsePublisher.PublishAsync(new SnapshotStatusNotification
        {
            SnapshotId = tracking.SnapshotId,
            AccountId = tracking.AccountId,
            ReceivedFiles = tracking.ReceivedFiles,
            MissingFiles = [],
            Status = SnapshotTrackingStatus.Complete,
            FirstReceivedAt = tracking.FirstReceivedAt,
            LastUpdatedAt = tracking.LastUpdatedAt,
            CompletedAt = completedAt,
        });

        _logger.LogInformation(
            "Published snapshot completion response snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType} receivedFileCount={ReceivedFileCount}",
            message.SnapshotId,
            message.AccountId,
            message.PayloadType,
            tracking.ReceivedFiles.Count);
    }
}
