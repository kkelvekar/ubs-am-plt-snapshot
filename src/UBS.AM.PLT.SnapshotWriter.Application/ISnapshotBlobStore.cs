using UBS.AM.PLT.SnapshotWriter.Domain;

namespace UBS.AM.PLT.SnapshotWriter.Application;

/// <summary>
/// Port for the blob write — step 1 of the strict write order. Writes the message's
/// opaque payload as a JSON file under the snapshot's folder and returns the snapshot
/// ROOT folder path (design §6: later slices store it as adls_root_path). The write is
/// idempotent: redelivery of the same message overwrites with identical content.
/// </summary>
public interface ISnapshotBlobStore
{
    Task<string> WriteAsync(SnapshotMessage message, CancellationToken cancellationToken);
}
