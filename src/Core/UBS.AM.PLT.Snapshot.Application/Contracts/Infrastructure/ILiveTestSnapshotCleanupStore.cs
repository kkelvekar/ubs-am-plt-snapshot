using UBS.AM.PLT.Snapshot.Application.Features.LiveTestCleanup;

namespace UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;

public interface ILiveTestSnapshotCleanupStore
{
    Task<IReadOnlyList<LiveTestSnapshotLocation>> ListAsync(CancellationToken cancellationToken);
    Task<int> DeleteAsync(string snapshotId, CancellationToken cancellationToken);
}
