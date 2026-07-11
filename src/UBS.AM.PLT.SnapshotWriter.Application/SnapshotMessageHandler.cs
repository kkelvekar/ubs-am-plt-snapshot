using Microsoft.Extensions.Logging;
using UBS.AM.PLT.SnapshotWriter.Application.Interfaces;
using UBS.AM.PLT.SnapshotWriter.Application.Interfaces.Infrastructure;
using UBS.AM.PLT.SnapshotWriter.Domain;

namespace UBS.AM.PLT.SnapshotWriter.Application;

/// <summary>
/// Orchestrates the strict write order per message:
/// blob write → tracking upsert → completeness check → index UPSERT.
/// Slice 2 implements step 1 (blob write); later slices add the rest. Any failure
/// propagates unchanged so the consumer never commits the offset (recovery is
/// forward, via redelivery).
/// </summary>
public sealed class SnapshotMessageHandler : ISnapshotMessageHandler
{
    private readonly ISnapshotBlobStore _blobStore;
    private readonly ILogger<SnapshotMessageHandler> _logger;

    public SnapshotMessageHandler(ISnapshotBlobStore blobStore, ILogger<SnapshotMessageHandler> logger)
    {
        _blobStore = blobStore;
        _logger = logger;
    }

    public async Task HandleAsync(SnapshotMessage message, CancellationToken cancellationToken)
    {
        var rootPath = await _blobStore.WriteAsync(message, cancellationToken);

        _logger.LogInformation(
            "Wrote snapshot payload blob snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType} rootPath={RootPath}",
            message.SnapshotId,
            message.AccountId,
            message.PayloadType,
            rootPath);
    }
}
