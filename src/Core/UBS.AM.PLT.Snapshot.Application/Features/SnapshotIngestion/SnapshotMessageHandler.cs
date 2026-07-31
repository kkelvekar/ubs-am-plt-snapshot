using Microsoft.Extensions.Logging;
using UBS.AM.PLT.Snapshot.Application.Contracts;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Application.Exceptions;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;

namespace UBS.AM.PLT.Snapshot.Application.Features.SnapshotIngestion;

/// <summary>
/// Orchestrates the write order for a single message: blob write, tracking upsert,
/// completeness check, index UPSERT. Any failure propagates unchanged so the consumer never
/// commits the offset and redelivery retries the message.
/// </summary>
/// <remarks>
/// A message refused by the pre-write envelope guards never enters that order: it is recorded
/// as a FAILED tracking row, reported to the publishing application, and the rejection is
/// rethrown for the consumer to commit past. No blob and no index row is touched on that path.
/// </remarks>
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
        try
        {
            SnapshotEnvelopeValidator.ValidateEnvelope(message);
            SnapshotEnvelopeValidator.ValidatePayloadType(message, _requiredFilesProvider);
        }
        catch (SnapshotMessageRejectedException ex)
        {
            await RecordAndPublishRejectionAsync(message, ex, cancellationToken);
            throw; // the consumer must still see the rejection and commit past it
        }

        // The root folder is pinned to the first payload's arrival time and reused by every
        // later or redelivered payload, so a snapshot whose messages straddle a month or year
        // boundary never splits across two folders. The lookup mutates nothing, so the write
        // order still starts at the blob write below.
        var rootPath = await _trackingStore.GetRootPathAsync(message.SnapshotId, cancellationToken)
            ?? SnapshotBlobPath.RootFolder(message, _timeProvider.GetUtcNow());
        await _blobStore.WriteAsync(message, rootPath, cancellationToken);
        var tracking = await _trackingStore.UpsertReceivedAsync(message, rootPath, cancellationToken);

        // A redelivery arriving after the snapshot already completed or failed does nothing
        // beyond the blob write and tracking upsert above.
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

                // The index UPSERT must precede the status flip: if it fails, tracking must
                // still read RECEIVING so redelivery retries this branch.
                await _indexStore.UpsertAsync(indexEntry, cancellationToken);

                // Publishing must also precede the flip: once the row reads COMPLETE this
                // branch stops firing, so a notification that failed after the flip could
                // never be retried.
                await PublishCompletedAsync(message, tracking);

                await _trackingStore.MarkCompleteAsync(message.SnapshotId, cancellationToken);

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
    /// Records a rejected message as a FAILED tracking row and tells the publishing
    /// application why the message was refused.
    /// </summary>
    /// <remarks>
    /// Neither step is swallowed: a failure in either propagates, so the offset is not
    /// committed and redelivery re-runs both. Both are idempotent, so the replay is harmless
    /// and a failed record can never silently drop the rejection. The FAILED row is the only
    /// durable write a rejection makes.
    /// </remarks>
    private async Task RecordAndPublishRejectionAsync(
        SnapshotMessage message,
        SnapshotMessageRejectedException rejection,
        CancellationToken cancellationToken)
    {
        // SnapshotId is the tracking primary key, so a null or over-long one has nothing to
        // record against and only the response goes out. A snapshotId rejected for its
        // characters still fits the column and is recorded: it is a usable key, just not a
        // usable blob path segment.
        var snapshotId = message.SnapshotId;
        var storable = snapshotId is not null && snapshotId.Length <= SnapshotFieldLimits.SnapshotIdMaxLength;

        SnapshotTrackingEntity? tracking = null;
        if (storable)
        {
            tracking = await _trackingStore.MarkRejectedAsync(
                new SnapshotRejectionRecord
                {
                    SnapshotId = snapshotId!,
                    AccountId = message.AccountId,
                    SnapshotType = message.SnapshotType,
                    ReasonCode = rejection.ReasonCode,
                    ReasonDetail = rejection.Message,
                },
                cancellationToken);
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        await _responsePublisher.PublishAsync(new SnapshotStatusNotification
        {
            // The message's own values, even when too broken to persist, so the publisher can
            // recognise which message this response is about.
            SnapshotId = snapshotId ?? string.Empty,
            AccountId = message.AccountId ?? string.Empty,
            ReceivedFiles = tracking?.ReceivedFiles ?? [],

            // Empty by design: this response reports a refused message, and the required-file
            // list may not be resolvable at all when snapshotType is itself the problem.
            MissingFiles = [],
            Status = SnapshotTrackingStatus.Failed,
            FirstReceivedAt = tracking?.FirstReceivedAt ?? now,
            LastUpdatedAt = tracking?.LastUpdatedAt ?? now,
            DeclaredFailedAt = tracking?.DeclaredFailedAt ?? now,
            ReasonCode = rejection.ReasonCode,
            ReasonDetail = rejection.Message,
        });

        _logger.LogInformation(
            "Published snapshot rejection response snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType} reasonCode={ReasonCode} recorded={Recorded}",
            message.SnapshotId,
            message.AccountId,
            message.PayloadType,
            rejection.ReasonCode,
            storable);
    }

    /// <summary>
    /// Announces that a snapshot has started arriving, emitted once on its first payload, so
    /// the publisher learns which files are still outstanding without waiting for completion.
    /// </summary>
    /// <remarks>
    /// "First" is keyed off the received-file count rather than the absence of a tracking row,
    /// because the count is stable under redelivery of that same payload and a failed publish
    /// is therefore retried instead of lost. The completing payload publishes <c>Complete</c>
    /// instead, never both.
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
    /// Tells the publishing application the snapshot is complete. <c>CompletedAt</c> is stamped
    /// from the same <see cref="TimeProvider"/> the tracking store uses a moment later, so the
    /// notification and the persisted <c>completed_at</c> agree. <c>MissingFiles</c> is always
    /// empty here.
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
