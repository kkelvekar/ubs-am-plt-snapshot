using Microsoft.Extensions.Logging;
using UBS.AM.PLT.Snapshot.Application.Contracts;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Application.Exceptions;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;

namespace UBS.AM.PLT.Snapshot.Application.Features.SnapshotIngestion;

/// <summary>
/// Orchestrates the write order for a single message: blob write, tracking upsert,
/// completeness check, index UPSERT. Processing failures propagate unchanged so the Kafka
/// command can classify them as transient, rejected, or poison.
/// </summary>
/// <remarks>
/// A message refused by the pre-write envelope guards, or by an invalid completed header, is
/// recorded as a FAILED tracking row, reported to the publishing application, and rethrown for
/// the consumer to commit past. The pre-write path touches no blob; a completed-header
/// rejection retains the payloads already written to durable storage.
/// </remarks>
public sealed class SnapshotMessageHandler : ISnapshotMessageHandler
{
    /// <summary>Reason code recorded for <see cref="RecordUnexpectedFailureAsync"/> — the poison path's fixed reason.</summary>
    public const string UnexpectedErrorReasonCode = "UNEXPECTED_ERROR";

    private readonly ISnapshotBlobStore _blobStore;
    private readonly ISnapshotTrackingStore _trackingStore;
    private readonly IRequiredFilesProvider _requiredFilesProvider;
    private readonly IPortfolioSnapshotIndexStore _indexStore;
    private readonly ISnapshotResponsePublisher _responsePublisher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SnapshotMessageHandler> _logger;

    public SnapshotMessageHandler(
        ISnapshotBlobStore blobStore,
        ISnapshotTrackingStore trackingStore,
        IRequiredFilesProvider requiredFilesProvider,
        IPortfolioSnapshotIndexStore indexStore,
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
                    var headerValues = PortfolioSnapshotIndexEntryBuilder.ExtractHeaderValues(headerJson);

                    var indexEntry = PortfolioSnapshotIndexEntryBuilder.Build(message, tracking, headerValues);

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
                        headerValues.EventType);
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
        catch (SnapshotMessageRejectedException ex)
        {
            await RecordAndPublishFailureAsync(message, ex.ReasonCode, ex.Message, cancellationToken);
            throw; // the consumer must still see the rejection and commit past it
        }
    }

    /// <summary>
    /// Records a message the Kafka command has decided is poison — a code defect or otherwise
    /// unclassified error, not a recognised rejection and not a transient infrastructure
    /// condition — under the fixed reason code <c>UNEXPECTED_ERROR</c>. Both the FAILED tracking
    /// row and Failed response are best effort. A failure in either operation is logged and
    /// swallowed so the caller can commit past the poison message instead of retrying forever.
    /// </summary>
    public async Task RecordUnexpectedFailureAsync(
        SnapshotMessage message,
        Exception exception,
        CancellationToken cancellationToken)
    {
        SnapshotTrackingEntity? tracking = null;

        try
        {
            tracking = await RecordFailureAsync(
                message,
                UnexpectedErrorReasonCode,
                exception.Message,
                cancellationToken);
        }
        catch (Exception recordException)
        {
            _logger.LogCritical(
                recordException,
                "Failed to record poison message in snapshot tracking; committing past it snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType} reasonCode={ReasonCode} originalError={OriginalError}",
                message.SnapshotId,
                message.AccountId,
                message.PayloadType,
                UnexpectedErrorReasonCode,
                exception.Message);
        }

        try
        {
            PublishFailure(message, UnexpectedErrorReasonCode, exception.Message, tracking);
        }
        catch (Exception publishException)
        {
            _logger.LogCritical(
                publishException,
                "Failed to publish poison message response; committing past it snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType} reasonCode={ReasonCode} originalError={OriginalError}",
                message.SnapshotId,
                message.AccountId,
                message.PayloadType,
                UnexpectedErrorReasonCode,
                exception.Message);
        }
    }

    /// <summary>
    /// Records a rejected message as a FAILED tracking row and tells the publishing application
    /// why the message failed.
    /// </summary>
    /// <remarks>
    /// A tracking-store failure propagates for a recognised rejection, so the offset is not
    /// committed and redelivery re-runs both steps; the upsert is idempotent, so the replay is
    /// harmless. Unexpected poison failures use <see cref="RecordUnexpectedFailureAsync"/>'s
    /// separate best-effort policy instead.
    /// </remarks>
    private async Task RecordAndPublishFailureAsync(
        SnapshotMessage message,
        string reasonCode,
        string reasonDetail,
        CancellationToken cancellationToken)
    {
        var tracking = await RecordFailureAsync(message, reasonCode, reasonDetail, cancellationToken);
        PublishFailure(message, reasonCode, reasonDetail, tracking);
    }

    private async Task<SnapshotTrackingEntity?> RecordFailureAsync(
        SnapshotMessage message,
        string reasonCode,
        string reasonDetail,
        CancellationToken cancellationToken)
    {
        // SnapshotId is the tracking primary key, so a null, empty or over-long one has
        // nothing to record against and only the response goes out. A snapshotId rejected for
        // its characters still fits the column and is recorded: it is a usable key, just not a
        // usable blob path segment.
        var snapshotId = message.SnapshotId;
        var storable = !string.IsNullOrEmpty(snapshotId) && snapshotId.Length <= SnapshotFieldLimits.SnapshotIdMaxLength;

        if (!storable)
        {
            return null;
        }

        return await _trackingStore.MarkRejectedAsync(
            new SnapshotRejectionRecord
            {
                SnapshotId = snapshotId!,
                AccountId = message.AccountId,
                SnapshotType = message.SnapshotType,
                ReasonCode = reasonCode,
                ReasonDetail = reasonDetail,
            },
            cancellationToken);
    }

    private void PublishFailure(
        SnapshotMessage message,
        string reasonCode,
        string reasonDetail,
        SnapshotTrackingEntity? tracking)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        _responsePublisher.Publish(new SnapshotStatusNotification
        {
            // The message's own values, even when too broken to persist, so the publisher can
            // recognise which message this response is about.
            SnapshotId = message.SnapshotId ?? string.Empty,
            AccountId = message.AccountId ?? string.Empty,
            ReceivedFiles = tracking?.ReceivedFiles ?? [],

            // Empty by design: this response reports a failed message, and the required-file
            // list may not be resolvable at all when snapshotType is itself the problem.
            MissingFiles = [],
            Status = SnapshotTrackingStatus.Failed,
            FirstReceivedAt = tracking?.FirstReceivedAt ?? now,
            LastUpdatedAt = tracking?.LastUpdatedAt ?? now,
            DeclaredFailedAt = tracking?.DeclaredFailedAt ?? now,
            ReasonCode = reasonCode,
            ReasonDetail = reasonDetail,
        });

        _logger.LogInformation(
            "Published snapshot failure response snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType} reasonCode={ReasonCode} recorded={Recorded}",
            message.SnapshotId,
            message.AccountId,
            message.PayloadType,
            reasonCode,
            tracking is not null);
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

        _responsePublisher.Publish(new SnapshotStatusNotification
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

        _responsePublisher.Publish(new SnapshotStatusNotification
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
