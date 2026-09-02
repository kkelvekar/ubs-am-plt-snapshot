namespace UBS.AM.PLT.Snapshot.IntegrationTests;

/// <summary>
/// Canonical valid <c>header</c> payload shared across test groups, so the header shape
/// lives in one place. The header is opaque to the writer: completion assertions compare
/// the persisted display data against <see cref="HeaderPayloadJson"/> verbatim, so no typed
/// twin of it exists.
/// </summary>
internal static class TestPayloads
{
    // The header's nested Payload object text, verbatim what the writer persists to
    // display_data (the envelope wrapper is never stored).
    public const string HeaderPayloadJson = """
        {
            "Event": "REBALANCE",
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

    public const string HeaderJson = $$"""
        {
          "SnapshotId": "corr20260713-0001",
          "Type": "Header",
          "Payload": {{HeaderPayloadJson}}
        }
        """;

    // The one header value the writer extracts from Payload.Event into its own filterable column; every other
    // header field reaches SQL only inside the verbatim DisplayData text.
    public const string HeaderEventType = "REBALANCE";

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

    public const string CompliancesJson = """
        {"checks":[{"rule":"MAX_ISSUER_WEIGHT","status":"PASS"}]}
        """;

    public const string OrdersHistoryJson = """
        {"orders":[{"id":"ORD-1","status":"SENT","at":"2026-07-13T10:50:00Z"}]}
        """;

    public const string PortfolioJson = """
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
