using Microsoft.Extensions.Logging;
using UBS.AM.PLT.SnapshotWriter.Application.Interfaces;
using UBS.AM.PLT.SnapshotWriter.Application.Interfaces.Infrastructure;
using UBS.AM.PLT.SnapshotWriter.Domain;

namespace UBS.AM.PLT.SnapshotWriter.Application;

/// <summary>
/// Orchestrates the strict write order per message:
/// blob write → tracking upsert → completeness check → index UPSERT.
/// Slices 1–3 implement steps 1–2 (blob write, then tracking upsert); later slices add
/// the rest. Any failure propagates unchanged so the consumer never commits the offset
/// (recovery is forward, via redelivery).
/// </summary>
public sealed class SnapshotMessageHandler : ISnapshotMessageHandler
{
    private readonly ISnapshotBlobStore _blobStore;
    private readonly ISnapshotTrackingStore _trackingStore;
    private readonly ILogger<SnapshotMessageHandler> _logger;

    public SnapshotMessageHandler(
        ISnapshotBlobStore blobStore,
        ISnapshotTrackingStore trackingStore,
        ILogger<SnapshotMessageHandler> logger)
    {
        _blobStore = blobStore;
        _trackingStore = trackingStore;
        _logger = logger;
    }

    public async Task HandleAsync(SnapshotMessage message, CancellationToken cancellationToken)
    {
        var rootPath = await _blobStore.WriteAsync(message, cancellationToken);
        var tracking = await _trackingStore.UpsertReceivedAsync(message, rootPath, cancellationToken);

        _logger.LogInformation(
            "Wrote snapshot payload blob and upserted tracking snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType} rootPath={RootPath} receivedFileCount={ReceivedFileCount}",
            message.SnapshotId,
            message.AccountId,
            message.PayloadType,
            rootPath,
            tracking.ReceivedFiles.Count);
    }
}
