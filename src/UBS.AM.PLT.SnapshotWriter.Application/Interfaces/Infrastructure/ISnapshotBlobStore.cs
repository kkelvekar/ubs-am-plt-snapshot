using UBS.AM.PLT.SnapshotWriter.Domain;

namespace UBS.AM.PLT.SnapshotWriter.Application.Interfaces.Infrastructure;

/// <summary>
/// Port for the blob write — step 1 of the strict write order. Writes the message's
/// opaque payload as a JSON file under <c>rootPath</c> — the snapshot ROOT
/// folder resolved by the handler (pinned to the first payload's arrival, design §6) —
/// so the adapter never derives a path itself. The write is idempotent: redelivery of
/// the same message overwrites with identical content.
/// </summary>
public interface ISnapshotBlobStore
{
    Task WriteAsync(SnapshotMessage message, string rootPath, CancellationToken cancellationToken);

    /// <summary>
    /// Reads back the header payload written for a snapshot — step 3 of the strict write
    /// order, called only once the snapshot is complete. Returns the raw header.json text
    /// (opaque string, no parsing at this layer); the caller deserialises it.
    /// </summary>
    Task<string> ReadHeaderAsync(string adlsRootPath, CancellationToken cancellationToken);
}
