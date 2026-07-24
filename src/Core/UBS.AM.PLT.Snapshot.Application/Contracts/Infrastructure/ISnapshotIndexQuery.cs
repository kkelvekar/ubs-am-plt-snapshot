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
}
