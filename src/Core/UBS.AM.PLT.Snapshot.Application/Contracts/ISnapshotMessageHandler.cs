using UBS.AM.PLT.Snapshot.Domain;

namespace UBS.AM.PLT.Snapshot.Application.Contracts;

/// <summary>
/// Application-layer entry point for one snapshot message. The Kafka adapter calls
/// straight into this port and commits the offset only when it completes successfully.
/// </summary>
public interface ISnapshotMessageHandler
{
    Task HandleAsync(SnapshotMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Records a message that failed for a reason other than a recognised rejection or a
    /// transient infrastructure condition — a code defect or an otherwise-unclassified error.
    /// The Kafka command calls this on the poison path, after deciding the failure is not worth
    /// retrying: it writes the same FAILED tracking row and Failed response shape as a rejection
    /// (reason code <c>UNEXPECTED_ERROR</c>), but — unlike <see cref="HandleAsync"/>'s rejection
    /// path — does not rethrow, since the caller already knows it is committing past the message.
    /// </summary>
    Task RecordUnexpectedFailureAsync(SnapshotMessage message, Exception exception, CancellationToken cancellationToken);
}
