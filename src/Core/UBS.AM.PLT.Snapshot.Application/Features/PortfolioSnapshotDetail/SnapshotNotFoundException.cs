namespace UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotDetail;

/// <summary>
/// Thrown when a snapshot-detail read finds nothing to return: no index row for the
/// snapshotId, or no such payload blob under its stored root. Constructed only through the
/// static factories so every message echoes just the caller-supplied identifiers - no storage
/// path, connection detail or other server-side value ever reaches the message. The Api edge
/// maps this to 404 NotFound.
/// </summary>
public sealed class SnapshotNotFoundException : Exception
{
    private SnapshotNotFoundException(string message) : base(message)
    {
    }

    /// <summary>No index row exists for the snapshotId - only COMPLETE snapshots have one.</summary>
    public static SnapshotNotFoundException ForSnapshot(string snapshotId)
        => new($"Snapshot '{snapshotId}' was not found.");

    /// <summary>The snapshot exists but stores no blob for that payload type.</summary>
    public static SnapshotNotFoundException ForPayload(string snapshotId, string payloadType)
        => new($"Snapshot '{snapshotId}' has no payload '{payloadType}'.");

    /// <summary>
    /// The snapshot has an index row but its root folder holds no payload files. An index row
    /// is written only once a snapshot is COMPLETE, so this is a stored-data inconsistency;
    /// it is still surfaced as not-found because there is nothing for the caller to render.
    /// </summary>
    public static SnapshotNotFoundException ForNoPayloads(string snapshotId)
        => new($"Snapshot '{snapshotId}' has no stored payloads.");
}
