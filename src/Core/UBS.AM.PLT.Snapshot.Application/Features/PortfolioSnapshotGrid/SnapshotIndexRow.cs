namespace UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotGrid;

/// <summary>
/// Read model for one dbo.SnapshotIndex row served to the Audit "Load snapshots"
/// grid (solution design section 7). Fixed fields come from real SQL columns; the non-filterable
/// display fields stay as the raw DisplayDataJson string and are flattened to
/// the response row inside this feature, so new display keys pass through with zero code
/// change. Deliberately free of ASP.NET types - this is an Application
/// contract, not a wire shape.
/// </summary>
public sealed class SnapshotIndexRow
{
    public required string SnapshotId { get; init; }
    public required string AccountId { get; init; }
    public DateTime SnapshotDate { get; init; }
    public required string EventType { get; init; }
    public required string AdlsPath { get; init; }
    public DateTime CreatedAt { get; init; }

    /// <summary>Raw DisplayData JSON column, opaque here; flattened by SnapshotRowFlattener.</summary>
    public required string DisplayDataJson { get; init; }
}
