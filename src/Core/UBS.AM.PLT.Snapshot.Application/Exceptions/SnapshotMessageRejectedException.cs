namespace UBS.AM.PLT.Snapshot.Application.Exceptions;

/// <summary>
/// Base type for a message that must not be retried: reprocessing it would fail identically
/// and block its partition, so the consumer commits the offset and moves on.
/// </summary>
/// <remarks>
/// This is the single category a consumer branches on — catch this type and commit, retry
/// everything else — so a new non-retryable case can be added without touching the consumer.
/// Abstract, so every rejection names a concrete reason.
/// </remarks>
public abstract class SnapshotMessageRejectedException : Exception
{
    protected SnapshotMessageRejectedException(string reasonCode, string message)
        : base(message)
    {
        ReasonCode = reasonCode;
    }

    protected SnapshotMessageRejectedException(string reasonCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ReasonCode = reasonCode;
    }

    /// <summary>
    /// Machine-readable cause, e.g. <c>NULL_REQUIRED_FIELD</c>. Logged, persisted in the FAILED
    /// tracking row's reason and sent to the publishing application, so values must stay stable
    /// across releases.
    /// </summary>
    public string ReasonCode { get; }
}
