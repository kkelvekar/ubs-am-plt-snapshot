using System.Text.Json;
using Microsoft.Extensions.Logging;
using UBS.AM.PLT.SnapshotWriter.Application;
using UBS.AM.PLT.SnapshotWriter.Domain;
using UBS.AM.PLT.SnapshotWriter.UnitTests.Fakes;
using Xunit;

namespace UBS.AM.PLT.SnapshotWriter.UnitTests;

public class SnapshotMessageHandlerTests
{
    [Fact]
    public async Task HandleAsync_writes_to_blob_store_exactly_once_with_the_incoming_message()
    {
        var blobStore = new FakeSnapshotBlobStore();
        var handler = CreateHandler(blobStore, new FakeSnapshotTrackingStore());
        var message = CreateMessage();

        await handler.HandleAsync(message, CancellationToken.None);

        var written = Assert.Single(blobStore.Written);
        Assert.Same(message, written);
    }

    [Fact]
    public async Task HandleAsync_upserts_tracking_with_the_root_path_returned_by_the_blob_store()
    {
        var trackingStore = new FakeSnapshotTrackingStore();
        var handler = CreateHandler(new FakeSnapshotBlobStore(), trackingStore);
        var message = CreateMessage();

        await handler.HandleAsync(message, CancellationToken.None);

        var upsert = Assert.Single(trackingStore.Upserts);
        Assert.Same(message, upsert.Message);
        Assert.Equal(SnapshotBlobPath.RootFolder(message), upsert.AdlsRootPath);
    }

    [Fact]
    public async Task HandleAsync_never_touches_tracking_when_the_blob_write_fails()
    {
        var blobStore = new FakeSnapshotBlobStore { ThrowOnWrite = new InvalidOperationException("blob endpoint unavailable") };
        var trackingStore = new FakeSnapshotTrackingStore();
        var handler = CreateHandler(blobStore, trackingStore);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(CreateMessage(), CancellationToken.None));

        Assert.Empty(trackingStore.Upserts);
    }

    [Fact]
    public async Task HandleAsync_propagates_blob_store_exception_unchanged()
    {
        var thrown = new InvalidOperationException("blob endpoint unavailable");
        var blobStore = new FakeSnapshotBlobStore { ThrowOnWrite = thrown };
        var logger = new CapturingLogger<SnapshotMessageHandler>();
        var handler = CreateHandler(blobStore, new FakeSnapshotTrackingStore(), logger);

        var caught = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(CreateMessage(), CancellationToken.None));

        Assert.Same(thrown, caught);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task HandleAsync_propagates_tracking_store_exception_unchanged()
    {
        var thrown = new InvalidOperationException("sql unavailable");
        var trackingStore = new FakeSnapshotTrackingStore { ThrowOnUpsert = thrown };
        var logger = new CapturingLogger<SnapshotMessageHandler>();
        var handler = CreateHandler(new FakeSnapshotBlobStore(), trackingStore, logger);

        var caught = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(CreateMessage(), CancellationToken.None));

        Assert.Same(thrown, caught);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task HandleAsync_logs_snapshotId_accountId_and_payloadType()
    {
        var logger = new CapturingLogger<SnapshotMessageHandler>();
        var handler = CreateHandler(new FakeSnapshotBlobStore(), new FakeSnapshotTrackingStore(), logger);

        await handler.HandleAsync(CreateMessage(), CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("corr98765", entry.State["SnapshotId"]);
        Assert.Equal("00675442A", entry.State["AccountId"]);
        Assert.Equal("instruments", entry.State["PayloadType"]);
    }

    private static SnapshotMessageHandler CreateHandler(
        FakeSnapshotBlobStore blobStore,
        FakeSnapshotTrackingStore trackingStore,
        CapturingLogger<SnapshotMessageHandler>? logger = null)
        => new(blobStore, trackingStore, logger ?? new CapturingLogger<SnapshotMessageHandler>());

    private static SnapshotMessage CreateMessage()
    {
        using var payload = JsonDocument.Parse("""{"total":21}""");
        return new SnapshotMessage
        {
            SnapshotId = "corr98765",
            AccountId = "00675442A",
            SnapshotType = "portfolio",
            PayloadType = "instruments",
            Stage = "PreTrade",
            PublishedAt = new DateTime(2026, 5, 22, 6, 10, 14, DateTimeKind.Utc),
            PublishedBy = "PortfolioCalculation",
            SchemaVersion = "1.0",
            Payload = payload.RootElement.Clone(),
        };
    }
}
