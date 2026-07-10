using Microsoft.Extensions.Logging;
using UBS.AM.PLT.SnapshotWriter.Domain;

namespace UBS.AM.PLT.SnapshotWriter.Application;

/// <summary>
/// No-op handler for slice 1. Later slices grow this into the strict write order:
/// blob write → tracking upsert → completeness check → index UPSERT.
/// </summary>
public sealed class SnapshotMessageHandler : ISnapshotMessageHandler
{
    private readonly ILogger<SnapshotMessageHandler> _logger;

    public SnapshotMessageHandler(ILogger<SnapshotMessageHandler> logger)
    {
        _logger = logger;
    }

    public Task HandleAsync(SnapshotMessage message, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Received snapshot payload snapshotId={SnapshotId} accountId={AccountId} payloadType={PayloadType}",
            message.SnapshotId,
            message.AccountId,
            message.PayloadType);

        return Task.CompletedTask;
    }
}
