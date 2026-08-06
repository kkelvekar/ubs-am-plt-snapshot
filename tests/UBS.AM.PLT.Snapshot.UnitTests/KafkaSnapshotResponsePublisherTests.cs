using Microsoft.Extensions.Logging;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;
using UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Publishing;
using UBS.AM.PLT.Snapshot.UnitTests.Fakes;
using Xunit;

namespace UBS.AM.PLT.Snapshot.UnitTests;

/// <summary>
/// The Application port adapter holds only the mapping: every Kafka mechanic lives in the
/// publish command it delegates to.
/// </summary>
public class KafkaSnapshotResponsePublisherTests
{
    [Fact]
    public void Notification_is_mapped_onto_the_org_response_and_keyed_by_accountId()
    {
        var command = new FakeSnapshotPublishCommand();
        var publisher = new KafkaSnapshotResponsePublisher(command, new CapturingLogger<KafkaSnapshotResponsePublisher>());

        publisher.Publish(CreateNotification());

        var executed = Assert.Single(command.Executed);
        Assert.Equal("00675442A", executed.Key);
        Assert.NotNull(executed.Value);
        Assert.Equal("corr98765", executed.Value.SnapshotId);
        Assert.Equal("Complete", executed.Value.Status);
    }

    [Fact]
    public void A_failed_publish_is_logged_and_never_thrown_at_the_caller()
    {
        // The caller is mid-write: a notification that could not be queued must not fail the
        // message it belongs to. Redelivery republishes it.
        var command = new FakeSnapshotPublishCommand { FailWith = "Local: Queue full" };
        var logger = new CapturingLogger<KafkaSnapshotResponsePublisher>();
        var publisher = new KafkaSnapshotResponsePublisher(command, logger);

        publisher.Publish(CreateNotification());

        var logged = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Equal("Local: Queue full", logged.State["Reason"]);
    }

    private static SnapshotStatusNotification CreateNotification()
        => new()
        {
            SnapshotId = "corr98765",
            AccountId = "00675442A",
            ReceivedFiles = ["header.json", "orders.json"],
            MissingFiles = [],
            Status = SnapshotTrackingStatus.Complete,
            FirstReceivedAt = new DateTime(2026, 5, 22, 6, 10, 14, DateTimeKind.Utc),
            LastUpdatedAt = new DateTime(2026, 5, 22, 6, 14, 22, DateTimeKind.Utc),
            CompletedAt = new DateTime(2026, 5, 22, 6, 14, 22, DateTimeKind.Utc),
        };
}
