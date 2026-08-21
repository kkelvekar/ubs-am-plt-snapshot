namespace UBS.AM.PLT.Snapshot.Domain.Entities;

/// <summary>
/// One row of the permanent snapshot_index table, per solution design §7: the audit UI's
/// grid data source, written once all required files for a snapshot are received. Plain
/// POCO — persistence mapping lives in Infrastructure.
/// </summary>
public class PortfolioSnapshotIndexEntity
{
    public required string SnapshotId { get; set; }
    public required string AccountId { get; set; }
    public DateTime SnapshotDate { get; set; }
    public required string EventType { get; set; }
    public required string AdlsPath { get; set; }

    /// <summary>
    /// The header's nested <c>Payload</c> object text, verbatim (the envelope —
    /// <c>SnapshotId</c>, <c>Type</c>, the <c>Payload</c> key itself — is not stored). Opaque
    /// JSON text carried as a string — the same treatment as <c>SnapshotMessage.Payload</c> —
    /// so every payload field reaches the audit UI without a C# type to widen. At completion,
    /// a scoped parse reads <c>Payload.Event</c> into <see cref="EventType"/> and
    /// <c>Payload</c>'s raw text into this property; neither is ever re-serialised.
    /// </summary>
    public required string DisplayData { get; set; }
    public DateTime CreatedAt { get; set; }
}
