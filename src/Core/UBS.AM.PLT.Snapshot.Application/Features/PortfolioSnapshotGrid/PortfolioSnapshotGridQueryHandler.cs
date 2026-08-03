using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Application.Contracts.Application;

namespace UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotGrid;

/// <summary>
/// Resolves the caller-supplied filter (SnapshotGridFilter.Resolve - 400-mappable
/// SnapshotGridFilterValidationException on an empty account list or inverted window),
/// reads via the IPortfolioSnapshotIndexQuery port, and flattens the opaque DisplayData JSON
/// into flat response rows via SnapshotRowFlattener. The Api controller only ever calls
/// this one method - it never talks to the read port or the flattener directly.
/// </summary>
public sealed class PortfolioSnapshotGridQueryHandler : IPortfolioSnapshotGridQueryHandler
{
    private readonly IPortfolioSnapshotIndexQuery _indexQuery;
    private readonly TimeProvider _timeProvider;

    public PortfolioSnapshotGridQueryHandler(IPortfolioSnapshotIndexQuery indexQuery, TimeProvider timeProvider)
    {
        _indexQuery = indexQuery;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> HandleAsync(
        SnapshotGridFilter requestedFilter, CancellationToken cancellationToken)
    {
        var resolved = SnapshotGridFilter.Resolve(requestedFilter, _timeProvider);
        var rows = await _indexQuery.QueryAsync(resolved, cancellationToken);
        return SnapshotRowFlattener.Flatten(rows);
    }
}
