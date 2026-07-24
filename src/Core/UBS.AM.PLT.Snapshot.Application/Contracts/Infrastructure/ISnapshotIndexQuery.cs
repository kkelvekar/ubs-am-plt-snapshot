using UBS.AM.PLT.Snapshot.Application.Models;

namespace UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;

/// <summary>
/// Read-side port for the Load-snapshots grid (solution design §7): returns the matching
/// <c>dbo.SnapshotIndex</c> rows for a resolved filter, newest first. Read-only — this port
/// never writes, and is registered by the Read API composition root only, never the worker.
/// The caller passes an already-resolved <see cref="SnapshotGridFilter"/>
/// (see <see cref="SnapshotGridFilter.Resolve"/>) with a concrete window and validated accounts.
/// </summary>
public interface ISnapshotIndexQuery
{
    Task<IReadOnlyList<SnapshotIndexRow>> QueryAsync(SnapshotGridFilter filter, CancellationToken ct);
}
