namespace UBS.AM.PLT.Snapshot.Application.Features.SnapshotIngestion;

/// <summary>
/// Maximum lengths for the identity fields, matching the column widths in
/// <c>db/scripts/001_snapshot_tracking.sql</c> and <c>db/scripts/002_snapshot_index.sql</c>.
/// The SQL scripts are the source of truth; a width change means updating the script, the EF
/// mapping and these constants together.
/// </summary>
/// <remarks>
/// Enforced before the first write, so an over-long value is rejected as a bad message instead
/// of failing a later SQL write that redelivery could never make succeed.
/// </remarks>
public static class SnapshotFieldLimits
{
    public const int SnapshotIdMaxLength = 100;   // varchar(100), both tables
    public const int AccountIdMaxLength = 100;    // varchar(100), both tables
    public const int SnapshotTypeMaxLength = 100; // varchar(100), SnapshotTracking
    public const int EventTypeMaxLength = 100;    // varchar(100), SnapshotIndex

    /// <summary>
    /// PayloadType has no column of its own — it becomes the <c>{payloadType}.json</c> blob
    /// filename and a received_files entry, both unbounded. Bounded to the same length anyway
    /// so no single path segment can grow without limit.
    /// </summary>
    public const int PayloadTypeMaxLength = 100;
}
