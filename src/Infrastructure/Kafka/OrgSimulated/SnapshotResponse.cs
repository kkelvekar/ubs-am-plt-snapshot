// Simulated org-shared-library type. At real lift-and-shift: delete this file, add NuGet
// reference `UBS.Advantage.CommunicationModels`. Never edit to fit our needs — adapt in the
// mapper instead.
namespace UBS.Advantage.CommunicationModels.Snapshot;

/// <summary>Response for a snapshot.</summary>
public class SnapshotResponse
{
    /// <summary>Unique identifier for the snapshot.</summary>
    public required string SnapshotId { get; set; }
    /// <summary>Account identifier for the snapshot.</summary>
    public string AccountId { get; set; } = string.Empty;
    /// <summary>List of received files for the snapshot.</summary>
    public string[] ReceivedFiles { get; set; } = [];
    /// <summary>List of missing files for the snapshot.</summary>
    public string[] MissingFiles { get; set; } = [];
    /// <summary>
    /// Status of the snapshot. Eg. "Receiving", "Complete", "Failed". "Failed" covers both a
    /// snapshot declared failed by the cleanup job and a single rejected message.
    /// </summary>
    public required string Status { get; set; }
    /// <summary>Timestamp when the snapshot was first received.</summary>
    public required string FirstReceivedAt { get; set; }
    /// <summary>Timestamp when the snapshot was last updated.</summary>
    public required string LastUpdatedAt { get; set; }
    /// <summary>Timestamp when the snapshot was completed.</summary>
    public string CompletedAt { get; set; } = string.Empty;
    /// <summary>Timestamp when the snapshot was declared failed.</summary>
    public string DeclaredFailedAt { get; set; } = string.Empty;
    // Contract extension shipped by the org team alongside the rejection responses: two
    // additive optional strings, so a consumer built against the previous version keeps
    // binding. Simulated here the same way the rest of this file is.
    /// <summary>Machine-readable cause when Status is "Failed". Eg. "FIELD_TOO_LONG". Empty otherwise.</summary>
    public string ReasonCode { get; set; } = string.Empty;
    /// <summary>Human-readable description of the failure cause. Empty unless Status is "Failed".</summary>
    public string ReasonDetail { get; set; } = string.Empty;
}
