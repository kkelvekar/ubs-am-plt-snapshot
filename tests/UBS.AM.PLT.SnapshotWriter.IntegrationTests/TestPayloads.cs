using UBS.AM.PLT.SnapshotWriter.Application.Models;
using UBS.AM.PLT.SnapshotWriter.Domain;

namespace UBS.AM.PLT.SnapshotWriter.IntegrationTests;

/// <summary>
/// Canonical valid <c>header</c> payload shared across test groups, so the required-field
/// shape (including <c>portfolioStatus</c>/<c>orderStatus</c>, which <see cref="HeaderPayload"/>
/// marks <c>required</c>) lives in one place. <see cref="ExpectedHeader"/> is the typed
/// twin of <see cref="HeaderJson"/> for field-by-field index assertions.
/// </summary>
internal static class TestPayloads
{
    public const string HeaderJson = """
        {
          "eventType": "REBALANCE",
          "portfolioStatus": "APPROVED",
          "orderStatus": "SENT",
          "benchmark": "MSCI World",
          "baseCcy": "CHF",
          "programId": "PRG-7",
          "batchId": "BATCH-2026-07-13",
          "numOrders": 17,
          "ptcAlerts": 2,
          "orderApprovedBy": "approver@ubs.com",
          "orderApprovedAt": "2026-07-13T10:45:00Z",
          "orderSentBy": "sender@ubs.com",
          "orderSentAt": "2026-07-13T10:50:00Z"
        }
        """;

    public static HeaderPayload ExpectedHeader { get; } = new()
    {
        EventType = "REBALANCE",
        PortfolioStatus = "APPROVED",
        OrderStatus = "SENT",
        Benchmark = "MSCI World",
        BaseCcy = "CHF",
        ProgramId = "PRG-7",
        BatchId = "BATCH-2026-07-13",
        NumOrders = 17,
        PtcAlerts = 2,
        OrderApprovedBy = "approver@ubs.com",
        OrderApprovedAt = new DateTime(2026, 7, 13, 10, 45, 0, DateTimeKind.Utc),
        OrderSentBy = "sender@ubs.com",
        OrderSentAt = new DateTime(2026, 7, 13, 10, 50, 0, DateTimeKind.Utc),
    };
}
