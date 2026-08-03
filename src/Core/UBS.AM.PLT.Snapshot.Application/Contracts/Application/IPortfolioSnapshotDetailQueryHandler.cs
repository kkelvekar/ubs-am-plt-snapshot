namespace UBS.AM.PLT.Snapshot.Application.Contracts.Application;

/// <summary>
/// Application-layer entry point for View-a-snapshot-detail (solution design section 10,
/// Screen 2). This is the single pair of methods the Api edge calls - each owns identifier
/// resolution, the snapshotId-to-AdlsPath lookup and the blob read, so no business logic or
/// wire-shaping decision lives in the Client project. Both methods return raw JSON text:
/// payloads stay opaque end to end and are never deserialised into a DTO, so the stored bytes
/// reach the caller exactly as they were written. Implemented by
/// PortfolioSnapshotDetailQueryHandler in the feature folder.
/// </summary>
public interface IPortfolioSnapshotDetailQueryHandler
{
    Task<string> GetPayloadAsync(string snapshotId, string payloadType, CancellationToken cancellationToken);

    Task<string> GetAllPayloadsAsync(string snapshotId, CancellationToken cancellationToken);
}
