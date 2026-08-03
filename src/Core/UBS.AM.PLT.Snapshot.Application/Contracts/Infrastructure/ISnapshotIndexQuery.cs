using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotGrid;

namespace UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;

/// <summary>
/// Port for the Load-snapshots grid read query (solution design section 7). Reads the
/// permanent snapshot_index table filtered by account, date range and optional event type.
/// The DisplayData JSON column stays opaque on this port (SnapshotIndexRow.DisplayDataJson) -
/// it is flattened by SnapshotRowFlattener, not here. The SQL implementation is
/// SnapshotIndexRepository in Infrastructure.
/// </summary>
public interface ISnapshotIndexQuery
{
    Task<IReadOnlyList<SnapshotIndexRow>> QueryAsync(SnapshotGridFilter filter, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a snapshotId to the AdlsPath stored on its index row - the blob root folder
    /// written at completion time (solution design section 10, Screen 2). Returns null when
    /// no index row exists, since only a COMPLETE snapshot ever gets one; the edge maps that
    /// to not-found. The stored path is used verbatim by the blob read - it is never
    /// recomputed from the snapshot identity, so a snapshot whose payloads straddled a
    /// month boundary still resolves to the one folder its files actually landed in.
    /// </summary>
    Task<string?> GetAdlsPathAsync(string snapshotId, CancellationToken cancellationToken);
}
