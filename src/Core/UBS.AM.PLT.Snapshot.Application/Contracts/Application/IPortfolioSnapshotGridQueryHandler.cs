using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotGrid;

namespace UBS.AM.PLT.Snapshot.Application.Contracts.Application;

/// <summary>
/// Application-layer entry point for the Load-snapshots grid (solution design section 7). This
/// is the single method the Api edge calls - it owns filter resolution, the read query and
/// the display-JSON flattening, so no business logic or wire-shaping decision lives in the
/// Client project. Implemented by PortfolioSnapshotGridQueryHandler in the feature folder.
/// </summary>
public interface IPortfolioSnapshotGridQueryHandler
{
    Task<IReadOnlyList<Dictionary<string, object?>>> HandleAsync(
        SnapshotGridFilter requestedFilter, CancellationToken cancellationToken);
}
