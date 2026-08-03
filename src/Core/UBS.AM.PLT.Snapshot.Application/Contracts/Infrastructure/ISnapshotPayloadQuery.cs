using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotDetail;

namespace UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;

/// <summary>
/// Read-only outbound port for fetching stored payload blobs (solution design section 10,
/// Screen 2). Deliberately separate from the write-flow ISnapshotBlobStore so the Read API
/// composition root never registers write semantics - a read process cannot create a
/// container or overwrite a payload. Both methods take the AdlsPath stored on the index row
/// verbatim and return the blob text exactly as stored: nothing is parsed or re-serialised at
/// this layer. The blob adapter is AzureBlobSnapshotStore in Infrastructure.Adls.
/// </summary>
public interface ISnapshotPayloadQuery
{
    /// <summary>
    /// Reads one <c>{payloadType}.json</c> blob under the snapshot root. Returns null when
    /// the blob does not exist, so the caller maps a missing payload to not-found rather
    /// than a server fault.
    /// </summary>
    Task<string?> ReadPayloadAsync(string adlsRootPath, string payloadType, CancellationToken cancellationToken);

    /// <summary>
    /// Reads every direct <c>*.json</c> child of the snapshot root. Returns an empty list
    /// when the root holds none (or does not exist) - never null.
    /// </summary>
    Task<IReadOnlyList<SnapshotPayloadFile>> ReadAllPayloadsAsync(string adlsRootPath, CancellationToken cancellationToken);
}
