namespace UBS.AM.PLT.Snapshot.Application.Models;

/// <summary>
/// Raised when a <see cref="SnapshotGridFilter"/> cannot be resolved into a safe query —
/// most importantly when no account is supplied, which would otherwise open the door to an
/// unbounded all-rows scan of the audit index. The Read API edge maps this to HTTP 400.
/// </summary>
public sealed class SnapshotGridFilterValidationException : Exception
{
    public SnapshotGridFilterValidationException(string message)
        : base(message)
    {
    }
}
