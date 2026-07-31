using UBS.AM.PLT.Snapshot.Domain;

namespace UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;

/// <summary>
/// Port for notifying the publishing application of a snapshot's status: RECEIVING on the
/// snapshot's first payload, COMPLETE on the payload that completes it, and FAILED when a
/// message is rejected.
/// </summary>
/// <remarks>
/// Every notification is published before the Kafka offset is committed, so a publish failure
/// leaves the offset uncommitted and redelivery retries the whole message. Notifications are
/// therefore at-least-once and consumers must tolerate duplicates.
///
/// The interface takes no <see cref="CancellationToken"/> because the publisher implementation
/// does not accept one.
/// </remarks>
public interface ISnapshotResponsePublisher
{
    /// <summary>Publishes a snapshot status notification to the publishing application.</summary>
    Task PublishAsync(SnapshotStatusNotification notification);
}
