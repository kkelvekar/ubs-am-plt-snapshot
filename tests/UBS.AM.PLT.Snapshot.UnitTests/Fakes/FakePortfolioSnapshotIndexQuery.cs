using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotGrid;

namespace UBS.AM.PLT.Snapshot.UnitTests.Fakes;

/// <summary>
/// In-memory <see cref="IPortfolioSnapshotIndexQuery"/> for the snapshot-detail handler tests:
/// <see cref="AdlsPaths"/> seeds the snapshotId-to-AdlsPath mapping and
/// <see cref="AdlsPathLookups"/> records the exact snapshotId each call received, so a test
/// can assert the value was resolved (trimmed) before it reached the port. The grid
/// <see cref="QueryAsync"/> is not exercised here and returns no rows.
/// </summary>
internal sealed class FakePortfolioSnapshotIndexQuery : IPortfolioSnapshotIndexQuery
{
    public Dictionary<string, string> AdlsPaths { get; } = new(StringComparer.Ordinal);

    public List<string> AdlsPathLookups { get; } = [];

    public Task<IReadOnlyList<PortfolioSnapshotIndexRow>> QueryAsync(SnapshotGridFilter filter, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<PortfolioSnapshotIndexRow>>([]);

    public Task<string?> GetAdlsPathAsync(string snapshotId, CancellationToken cancellationToken)
    {
        AdlsPathLookups.Add(snapshotId);
        AdlsPaths.TryGetValue(snapshotId, out var adlsPath);
        return Task.FromResult(adlsPath);
    }
}
