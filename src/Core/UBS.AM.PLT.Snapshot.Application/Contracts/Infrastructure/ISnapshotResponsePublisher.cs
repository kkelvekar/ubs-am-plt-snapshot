using UBS.AM.PLT.Snapshot.Domain;

namespace UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;

/// <summary>
/// Port for notifying the publishing application of a snapshot's status. Called after the
/// snapshot's tracking row is flipped to COMPLETE and before the Kafka offset is committed:
/// a publish failure propagates, no offset is committed, and redelivery retries the whole
/// message. Every preceding write is idempotent, so that replay is harmless — but the
/// notification itself is at-least-once and its consumer must tolerate duplicates.
/// </summary>
/// <remarks>
/// No <see cref="CancellationToken"/> parameter: the org publisher this is swapped for at
/// lift-and-shift does not take one, and the port must not promise what its implementation
/// cannot honour.
/// </remarks>
public interface ISnapshotResponsePublisher
{
    Task PublishAsync(SnapshotStatusNotification notification);
}
