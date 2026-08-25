namespace UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;

/// <summary>
/// Port for deciding whether an exception raised while processing a snapshot message
/// represents a transient infrastructure condition (worth parking the consumer and letting
/// Kafka redeliver) versus a poison message (would fail identically forever, so the offset
/// commits past it). Vendor-neutral by design: Application and Domain never reference
/// <c>SqlException</c> or <c>RequestFailedException</c> directly. Each vendor-specific
/// infrastructure project registers its own implementation under this port; the Kafka command
/// aggregates every registered classifier and treats an exception as transient if any one of
/// them says so, defaulting to poison when none matches.
/// </summary>
public interface ITransientFailureClassifier
{
    /// <summary>
    /// True when <paramref name="exception"/> — or an exception anywhere in its inner-exception
    /// chain — represents a condition expected to resolve on its own (network blip, throttling,
    /// broker/database temporarily unreachable). Implementations must walk the inner-exception
    /// chain: EF Core wraps <c>SqlException</c> in <c>DbUpdateException</c> on
    /// <c>SaveChangesAsync</c>, so a top-level-only check would misclassify a real SQL outage as
    /// a poison message.
    /// </summary>
    bool IsTransient(Exception exception);
}
