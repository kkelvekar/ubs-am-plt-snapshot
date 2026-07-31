namespace UBS.AM.PLT.Snapshot.Application.Features.SnapshotIngestion;

/// <summary>
/// Maximum lengths for the identity fields, matching the column widths in
/// <c>db/scripts/001_snapshot_tracking.sql</c> and <c>db/scripts/002_snapshot_index.sql</c>.
/// The SQL scripts are the source of truth; a width change means updating the script, the
/// EF mapping and these constants together (grep <c>SnapshotFieldLimits</c> to find all three).
/// </summary>
/// <remarks>
/// Checked before the first write, so an over-long value is rejected as a bad message rather
/// than surfacing later as a SQL truncation error on a write that can never succeed. That is
/// what keeps every post-write failure uniformly retryable, with no SQL-error-code coupling.
/// </remarks>
public static class SnapshotFieldLimits
{
    public const int SnapshotIdMaxLength = 100;   // varchar(100), both tables — SnapshotId is a GUID (36 chars) with headroom
    public const int AccountIdMaxLength = 100;    // varchar(100), both tables
    public const int SnapshotTypeMaxLength = 100; // varchar(100), SnapshotTracking
    public const int EventTypeMaxLength = 100;    // varchar(100), SnapshotIndex

    /// <summary>
    /// PayloadType has no column of its own — it becomes the <c>{payloadType}.json</c> blob
    /// filename and a received_files entry (both unbounded columns). Bounded to the same
    /// length anyway so no single path segment can grow without limit.
    /// </summary>
    public const int PayloadTypeMaxLength = 100;
}
