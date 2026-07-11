using System.Text.Json;

namespace UBS.AM.PLT.SnapshotWriter.Domain;

/// <summary>
/// Kafka message envelope, per solution design §4. Every message uses this envelope
/// regardless of publisher or payload type. <see cref="Payload"/> is opaque and is
/// written to blob via <c>GetRawText()</c> without parsing — only the header payload
/// is ever deserialised, and only at completion time.
/// </summary>
public class SnapshotMessage
{
    // Envelope -- fixed, always present
    public required string SnapshotId { get; set; }  // correlationId
    public required string AccountId { get; set; }
    public required string SnapshotType { get; set; }  // "portfolio"
    public required string PayloadType { get; set; }  // "header" / "instruments" etc
    public required string Stage { get; set; }  // "PreTrade" etc
    public required DateTime PublishedAt { get; set; }
    public required string PublishedBy { get; set; }  // "PortfolioCalculation"
    public required string SchemaVersion { get; set; }  // "1.0"

    // Payload -- opaque, variable per payloadType
    public required JsonElement Payload { get; set; }
}
