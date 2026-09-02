namespace UBS.AM.PLT.Snapshot.Domain;

/// <summary>
/// Kafka message envelope, per solution design §4 and the org-approved JSON schema
/// (<c>docs/snapshot-request.schema.json</c>): seven string properties, PascalCase on the
/// wire. <see cref="Payload"/> is opaque JSON <em>text</em> — already serialised by the
/// publisher and written to blob verbatim, never re-serialised. Only the header payload is
/// ever deserialised, and only at completion time.
/// </summary>
public class SnapshotMessage
{
    // Envelope -- fixed, always present
    public required string SnapshotId { get; set; }  // correlationId
    public required string AccountId { get; set; }
    public required string SnapshotType { get; set; }  // "portfolio"
    public required string PayloadType { get; set; }  // "header" / "orders-history" etc
    public required string PublishedAt { get; set; }  // log-only, never used in logic
    public required string PublishedBy { get; set; }  // "PortfolioCalculation"

    // Payload -- opaque JSON text, variable per payloadType
    public required string Payload { get; set; }
}
