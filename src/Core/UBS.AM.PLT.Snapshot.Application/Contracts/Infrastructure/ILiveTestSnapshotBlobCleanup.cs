using UBS.AM.PLT.Snapshot.Application.Features.LiveTestCleanup;

namespace UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;

public interface ILiveTestSnapshotBlobCleanup
{
    Task<IReadOnlyList<LiveTestSnapshotLocation>> ListAsync(CancellationToken cancellationToken);
    Task<int> DeleteAsync(LiveTestSnapshotLocation location, CancellationToken cancellationToken);
}
