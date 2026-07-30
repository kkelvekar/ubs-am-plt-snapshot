using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Domain;

namespace UBS.AM.PLT.Snapshot.UnitTests.Fakes;

public sealed class FakeSnapshotResponsePublisher : ISnapshotResponsePublisher
{
    private readonly List<SnapshotStatusNotification> _published = [];

    public IReadOnlyList<SnapshotStatusNotification> Published => _published;

    public Exception? ThrowOnPublish { get; set; }

    /// <summary>
    /// Shared call-order log, injected by the test, so the publish can be asserted to land
    /// after the index UPSERT and before <see cref="FakeSnapshotTrackingStore.MarkCompleteAsync"/>.
    /// </summary>
    public List<string>? CallOrderLog { get; set; }

    public Task PublishAsync(SnapshotStatusNotification notification)
    {
        CallOrderLog?.Add(nameof(PublishAsync));

        if (ThrowOnPublish is not null)
        {
            throw ThrowOnPublish;
        }

        _published.Add(notification);
        return Task.CompletedTask;
    }
}
