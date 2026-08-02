using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Domain;

namespace UBS.AM.PLT.Snapshot.IntegrationTests.Support;

/// <summary>
/// Stands in for the Kafka publisher in Mode A, where the broker is bypassed exactly as the
/// consumer is: the assertion of interest is that the Application layer emits the right
/// notification at the right point in the write order, not that librdkafka can reach a
/// broker (Mode B covers that). Shared by the run-scoped <see cref="SnapshotFixture"/>, so
/// it is thread-safe and scenarios filter what they read by snapshotId.
/// </summary>
public sealed class RecordingSnapshotResponsePublisher : ISnapshotResponsePublisher
{
    private readonly Lock _gate = new();
    private readonly List<SnapshotStatusNotification> _published = [];

    public void Publish(SnapshotStatusNotification notification)
    {
        lock (_gate)
        {
            _published.Add(notification);
        }
    }

    public IReadOnlyList<SnapshotStatusNotification> PublishedFor(string snapshotId)
    {
        lock (_gate)
        {
            return [.. _published.Where(n => string.Equals(n.SnapshotId, snapshotId, StringComparison.Ordinal))];
        }
    }
}
