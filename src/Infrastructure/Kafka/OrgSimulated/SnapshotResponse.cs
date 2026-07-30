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
    /// <summary>Status of the snapshot. Eg. "Receiving", "Complete", "Failed".</summary>
    public required string Status { get; set; }
    /// <summary>Timestamp when the snapshot was first received.</summary>
    public required string FirstReceivedAt { get; set; }
    /// <summary>Timestamp when the snapshot was last updated.</summary>
    public required string LastUpdatedAt { get; set; }
    /// <summary>Timestamp when the snapshot was completed.</summary>
    public string CompletedAt { get; set; } = string.Empty;
    /// <summary>Timestamp when the snapshot was declared failed.</summary>
    public string DeclaredFailedAt { get; set; } = string.Empty;
}
