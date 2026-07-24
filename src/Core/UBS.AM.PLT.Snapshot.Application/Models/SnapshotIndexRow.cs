namespace UBS.AM.PLT.Snapshot.Application.Models;

/// <summary>
/// Read model for one <c>dbo.SnapshotIndex</c> row served to the Audit "Load snapshots"
/// grid (solution design §7). Fixed fields come from real SQL columns; the non-filterable
/// display fields stay as the raw <see cref="DisplayDataJson"/> string and are flattened to
/// the response row at the client edge, so new display keys pass through with zero code
/// change. Deliberately free of System.Text.Json / ASP.NET types — this is an Application
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

    /// <summary>Raw <c>DisplayData</c> JSON column, opaque here; flattened at the edge.</summary>
    public required string DisplayDataJson { get; init; }
}
