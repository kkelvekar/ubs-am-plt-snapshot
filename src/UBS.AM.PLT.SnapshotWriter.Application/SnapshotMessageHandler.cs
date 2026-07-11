using System.Text.Json;
using Microsoft.Extensions.Logging;
using UBS.AM.PLT.SnapshotWriter.Application.Interfaces;
using UBS.AM.PLT.SnapshotWriter.Application.Interfaces.Infrastructure;
using UBS.AM.PLT.SnapshotWriter.Domain;

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
    private readonly ILogger<SnapshotMessageHandler> _logger;

    public SnapshotMessageHandler(
        ISnapshotBlobStore blobStore,
        ISnapshotTrackingStore trackingStore,
        IRequiredFilesProvider requiredFilesProvider,
        ISnapshotIndexStore indexStore,
        ILogger<SnapshotMessageHandler> logger)
    {
        _blobStore = blobStore;
        _trackingStore = trackingStore;
        _requiredFilesProvider = requiredFilesProvider;
        _indexStore = indexStore;
        _logger = logger;
    }

    public async Task HandleAsync(SnapshotMessage message, CancellationToken cancellationToken)
    {
        var rootPath = await _blobStore.WriteAsync(message, cancellationToken);
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
            "Wrote snapshot payload blob and upserted tracking snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType} rootPath={RootPath} receivedFileCount={ReceivedFileCount}",
            message.SnapshotId,
            message.AccountId,
            message.PayloadType,
            rootPath,
            tracking.ReceivedFiles.Count);
    }

    private static SnapshotIndexEntry BuildIndexEntry(
        SnapshotMessage message,
        SnapshotTrackingEntry tracking,
        HeaderPayload header)
        => new()
        {
            SnapshotId = message.SnapshotId,
            AccountId = message.AccountId,
            SnapshotDate = tracking.FirstReceivedAt,
            Stage = message.Stage,
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
