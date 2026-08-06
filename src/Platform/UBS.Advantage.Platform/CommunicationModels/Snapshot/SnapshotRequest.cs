namespace UBS.Advantage.CommunicationModels.Snapshot;

/// <summary>Request for a snapshot.</summary>
public class SnapshotRequest
{
    /// <summary>Unique identifier for the snapshot.</summary>
    public required string SnapshotId { get; set; }
    /// <summary>Account identifier for the snapshot.</summary>
    public string AccountId { get; set; } = string.Empty;
    /// <summary>Type of the snapshot. E.g. "portfolio", "optimizer".</summary>
    public required string SnapshotType { get; set; }
    /// <summary>Type of the payload for the snapshot. E.g. header, orders, portfolio, calculations, settings etc.</summary>
    public required string PayloadType { get; set; }
    public string PublishedAt { get; set; } = string.Empty;
    public string PublishedBy { get; set; } = string.Empty;
    public required string Payload { get; set; }
}
