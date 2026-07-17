namespace UBS.AM.PLT.Snapshot.Domain.Entities;

/// <summary>
/// One row of the permanent snapshot_index table, per solution design §7: the audit UI's
/// grid data source, written once all required files for a snapshot are received. Plain
/// POCO — persistence mapping lives in Infrastructure.
/// </summary>
public class SnapshotIndexEntity
{
    public required string SnapshotId { get; set; }
    public required string AccountId { get; set; }
    public DateTime SnapshotDate { get; set; }
    public required string EventType { get; set; }
    public required string AdlsPath { get; set; }
    public required SnapshotIndexDisplayData DisplayData { get; set; }
    public DateTime CreatedAt { get; set; }
}
