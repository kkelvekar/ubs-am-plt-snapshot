using UBS.AM.PLT.Snapshot.Application.Models;
using UBS.AM.PLT.Snapshot.Domain;

namespace UBS.AM.PLT.Snapshot.IntegrationTests;

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

    // Canned opaque required-file payloads. No scenario asserts on their internal fields
    // (payloads stay opaque and are only compared byte-for-byte against what was sent), so a
    // single canonical body per required payloadType is sufficient — the feature files name the
    // payloadType, the step definitions supply the body from here.
    public const string OrdersJson = """
        {"positions":[{"isin":"CH0038863350","qty":250}]}
        """;

    // A second, deliberately DIFFERENT orders body. Used only where a scenario must prove
    // two snapshots never cross-contaminate, so their orders blobs must differ.
    public const string OrdersJsonAlt = """
        {"positions":[{"isin":"US5949181045","qty":400}]}
        """;

    public const string CalculationsJson = """
        {"nav":5555.55,"ccy":"CHF"}
        """;

    public const string SettingsJson = """
        {"tolerance":0.05}
        """;

    // An out-of-contract payload: a payloadType (auditlog) that is not part of the required-files
    // set, stored opaquely but ignored by the completeness check.
    public const string AuditLogJson = """
        {"entries":[{"at":"2026-07-14T10:00:00Z","by":"system"}]}
        """;
}
