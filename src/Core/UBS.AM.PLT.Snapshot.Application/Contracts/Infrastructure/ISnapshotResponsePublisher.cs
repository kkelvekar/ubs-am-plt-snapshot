using UBS.AM.PLT.Snapshot.Domain;

namespace UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;

/// <summary>
/// Port for notifying the publishing application of a snapshot's status: RECEIVING on the
/// snapshot's first payload, COMPLETE on the payload that completes it, and FAILED when a
/// message is rejected.
/// </summary>
/// <remarks>
/// Every notification is published before the Kafka offset is committed. Publishing is
/// fire-and-forget, matching the org publisher's own synchronous contract: the call queues
/// the notification and returns immediately, so a delivery failure surfaces only in the
/// publisher's own logging, not as an exception the caller can react to.
///
/// The interface takes no <see cref="CancellationToken"/> because the publisher implementation
/// does not accept one.
/// </remarks>
public interface ISnapshotResponsePublisher
{
    /// <summary>Publishes a snapshot status notification to the publishing application.</summary>
    void Publish(SnapshotStatusNotification notification);
}
