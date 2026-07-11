using UBS.AM.PLT.SnapshotWriter.Domain;

namespace UBS.AM.PLT.SnapshotWriter.Application.Interfaces.Infrastructure;

/// <summary>
/// Port for the tracking upsert — step 2 of the strict write order, called only after
/// the blob write succeeded. First payload for a snapshot INSERTs a RECEIVING row with
/// <paramref name="adlsRootPath"/>; subsequent payloads only set-union the payload's
/// filename into received_files and advance last_updated_at (never touching status,
/// adls_root_path or first_received_at), so redelivery at any point is harmless. Returns
/// the post-upsert entry for the completeness check that follows (later slice).
/// </summary>
public interface ISnapshotTrackingStore
{
    Task<SnapshotTrackingEntry> UpsertReceivedAsync(SnapshotMessage message, string adlsRootPath, CancellationToken cancellationToken);
}
