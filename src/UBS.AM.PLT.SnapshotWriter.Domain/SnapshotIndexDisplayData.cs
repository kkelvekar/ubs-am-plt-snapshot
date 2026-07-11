namespace UBS.AM.PLT.SnapshotWriter.Domain;

/// <summary>
/// Non-filterable grid fields persisted as the snapshot_index row's single JSON display
/// column, per solution design §7. Built 1:1 from <see cref="HeaderPayload"/> at
/// completion time.
/// </summary>
public class SnapshotIndexDisplayData
{
    public required string Benchmark { get; set; }
    public required string BaseCcy { get; set; }
    public required string ProgramId { get; set; }
    public required string BatchId { get; set; }
    public int NumOrders { get; set; }
    public int PtcAlerts { get; set; }
    public required string OrderApprovedBy { get; set; }
    public DateTime? OrderApprovedAt { get; set; }
    public required string OrderSentBy { get; set; }
    public DateTime? OrderSentAt { get; set; }
}
