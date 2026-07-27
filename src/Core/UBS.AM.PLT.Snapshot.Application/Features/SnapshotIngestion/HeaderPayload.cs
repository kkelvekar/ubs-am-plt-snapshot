namespace UBS.AM.PLT.Snapshot.Application.Features.SnapshotIngestion;

/// <summary>
/// Deserialised shape of the <c>header</c> payload, per solution design §4. This is the
/// only payload ever deserialised, and only at completion time when building the
/// snapshot_index row (design §7). All other payloads stay opaque JSON text.
/// <see cref="OrderApprovedAt"/> and <see cref="OrderSentAt"/> are deliberately nullable —
/// unlike the design doc's literal (non-nullable) C# contract — because the doc's own
/// wire-format example omits <c>orderSentAt</c>; nullable + tolerate-absence avoids
/// silently defaulting an absent timestamp to <c>0001-01-01</c>.
/// </summary>
public class HeaderPayload
{
    public required string EventType { get; set; }
    public required string PortfolioStatus { get; set; }
    public required string OrderStatus { get; set; }
    public required string Benchmark { get; set; }
    public required string BaseCcy { get; set; }
    public required string OrderApprovedBy { get; set; }
    public DateTime? OrderApprovedAt { get; set; }
    public required string OrderSentBy { get; set; }
    public DateTime? OrderSentAt { get; set; }
    public required string ProgramId { get; set; }
    public required string BatchId { get; set; }
    public int NumOrders { get; set; }
    public int PtcAlerts { get; set; }
}
