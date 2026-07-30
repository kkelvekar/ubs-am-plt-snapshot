namespace UBS.AM.PLT.Snapshot.Application.Exceptions;

/// <summary>
/// Marks a message that must NOT be retried: reprocessing it would fail identically forever
/// and block its partition, so the consumer's correct response is to stop, commit the offset
/// and let the partition move on.
///
/// This is the single category a consumer branches on
/// (<c>catch (SnapshotMessageRejectedException) -&gt; commit</c>, everything else -&gt; retry),
/// and it lives in Application deliberately: at org lift-and-shift the Kafka adapter and its
/// consumer are replaced while this layer ships unchanged, so the replacement consumer has a
/// stable type to catch. Catching the category rather than a specific reason means a new
/// non-retryable case added later is handled without touching the consumer.
///
/// Abstract so every rejection names a concrete reason instead of throwing the bare category.
/// </summary>
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
    /// Stable machine-readable cause, e.g. <c>NULL_REQUIRED_FIELD</c>. Logged today; it is
    /// what the rejection response on the Kafka response topic will carry once failure
    /// responses are added, so it is worth keeping stable across releases.
    /// </summary>
    public string ReasonCode { get; }
}
