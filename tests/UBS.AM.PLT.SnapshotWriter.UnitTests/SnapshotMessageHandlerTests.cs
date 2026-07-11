using System.Text.Json;
using Microsoft.Extensions.Logging;
using UBS.AM.PLT.SnapshotWriter.Application;
using UBS.AM.PLT.SnapshotWriter.Domain;
using UBS.AM.PLT.SnapshotWriter.UnitTests.Fakes;
using Xunit;

namespace UBS.AM.PLT.SnapshotWriter.UnitTests;

public class SnapshotMessageHandlerTests
{
    private static readonly HashSet<string> PortfolioRequiredFiles =
        ["header.json", "instruments.json", "calculations.json", "settings.json"];

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
        var handler = CreateHandler(blobStore, new FakeSnapshotTrackingStore(), logger: logger);

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
        var handler = CreateHandler(new FakeSnapshotBlobStore(), trackingStore, logger: logger);

        var caught = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(CreateMessage(), CancellationToken.None));

        Assert.Same(thrown, caught);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task HandleAsync_logs_snapshotId_accountId_and_payloadType()
    {
        var logger = new CapturingLogger<SnapshotMessageHandler>();
        var handler = CreateHandler(new FakeSnapshotBlobStore(), new FakeSnapshotTrackingStore(), logger: logger);

        await handler.HandleAsync(CreateMessage(), CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("corr98765", entry.State["SnapshotId"]);
        Assert.Equal("00675442A", entry.State["AccountId"]);
        Assert.Equal("instruments", entry.State["PayloadType"]);
    }

    [Fact]
    public async Task HandleAsync_does_not_touch_index_or_mark_complete_when_receiving_and_incomplete()
    {
        var trackingStore = new FakeSnapshotTrackingStore
        {
            StatusToReturn = SnapshotTrackingStatus.Receiving,
            ReceivedFilesToReturn = ["header.json", "instruments.json"],
        };
        var requiredFilesProvider = new FakeRequiredFilesProvider();
        requiredFilesProvider.RequiredFilesByType["portfolio"] = PortfolioRequiredFiles;
        var indexStore = new FakeSnapshotIndexStore();
        var blobStore = new FakeSnapshotBlobStore();

        var handler = CreateHandler(blobStore, trackingStore, requiredFilesProvider, indexStore);

        await handler.HandleAsync(CreateMessage(), CancellationToken.None);

        Assert.Empty(blobStore.HeaderReadsFor);
        Assert.Empty(indexStore.Upserts);
        Assert.Empty(trackingStore.MarkedComplete);
    }

    [Fact]
    public async Task HandleAsync_when_complete_reads_header_via_blob_store_and_upserts_index_before_marking_complete()
    {
        var callOrderLog = new List<string>();
        var trackingStore = new FakeSnapshotTrackingStore
        {
            StatusToReturn = SnapshotTrackingStatus.Receiving,
            ReceivedFilesToReturn = ["header.json", "instruments.json", "calculations.json", "settings.json"],
            CallOrderLog = callOrderLog,
        };
        var requiredFilesProvider = new FakeRequiredFilesProvider();
        requiredFilesProvider.RequiredFilesByType["portfolio"] = PortfolioRequiredFiles;
        var indexStore = new FakeSnapshotIndexStore { CallOrderLog = callOrderLog };

        const string headerJson = """
            {
              "eventType":       "ModelChange",
              "portfolioStatus": "ReadyToSend",
              "orderStatus":     "ReadyToSend",
              "benchmark":       "MCCHM2EQ",
              "baseCcy":         "CHF",
              "orderApprovedBy": "Anna Miller",
              "orderApprovedAt": "2026-05-15T06:10:14Z",
              "orderSentBy":     "James Smith",
              "numOrders":       4,
              "ptcAlerts":       0,
              "programId":       "123456",
              "batchId":         "15884"
            }
            """;
        var blobStore = new FakeSnapshotBlobStore { HeaderJson = headerJson };

        // Deliberately the completing message is itself the header payload — the handler
        // must still re-fetch header.json via the blob store, never via message.Payload.
        var message = CreateMessage(payloadType: "header", payload: """{"eventType":"ShouldNeverBeUsed"}""");

        var handler = CreateHandler(blobStore, trackingStore, requiredFilesProvider, indexStore);

        await handler.HandleAsync(message, CancellationToken.None);

        var expectedRootPath = SnapshotBlobPath.RootFolder(message);
        Assert.Equal([expectedRootPath], blobStore.HeaderReadsFor);

        var indexEntry = Assert.Single(indexStore.Upserts);
        Assert.Equal(message.SnapshotId, indexEntry.SnapshotId);
        Assert.Equal(message.AccountId, indexEntry.AccountId);
        Assert.Equal(new DateTime(2026, 5, 22, 6, 10, 14, DateTimeKind.Utc), indexEntry.SnapshotDate);
        Assert.Equal(message.Stage, indexEntry.Stage);
        Assert.Equal("ModelChange", indexEntry.EventType);
        Assert.Equal(expectedRootPath, indexEntry.AdlsPath);
        Assert.Equal("MCCHM2EQ", indexEntry.DisplayData.Benchmark);
        Assert.Equal("CHF", indexEntry.DisplayData.BaseCcy);
        Assert.Equal("123456", indexEntry.DisplayData.ProgramId);
        Assert.Equal("15884", indexEntry.DisplayData.BatchId);
        Assert.Equal(4, indexEntry.DisplayData.NumOrders);
        Assert.Equal(0, indexEntry.DisplayData.PtcAlerts);
        Assert.Equal("Anna Miller", indexEntry.DisplayData.OrderApprovedBy);
        Assert.Equal(new DateTime(2026, 5, 15, 6, 10, 14, DateTimeKind.Utc), indexEntry.DisplayData.OrderApprovedAt);
        Assert.Equal("James Smith", indexEntry.DisplayData.OrderSentBy);
        Assert.Null(indexEntry.DisplayData.OrderSentAt);

        Assert.Equal([message.SnapshotId], trackingStore.MarkedComplete);
        Assert.Equal([nameof(FakeSnapshotIndexStore.UpsertAsync), nameof(FakeSnapshotTrackingStore.MarkCompleteAsync)], callOrderLog);
    }

    [Fact]
    public async Task HandleAsync_skips_completeness_check_entirely_when_tracking_already_complete()
    {
        var trackingStore = new FakeSnapshotTrackingStore { StatusToReturn = SnapshotTrackingStatus.Complete };
        var requiredFilesProvider = new FakeRequiredFilesProvider();
        requiredFilesProvider.RequiredFilesByType["portfolio"] = PortfolioRequiredFiles;
        var indexStore = new FakeSnapshotIndexStore();
        var blobStore = new FakeSnapshotBlobStore();

        var handler = CreateHandler(blobStore, trackingStore, requiredFilesProvider, indexStore);

        await handler.HandleAsync(CreateMessage(), CancellationToken.None);

        Assert.Empty(requiredFilesProvider.Calls);
        Assert.Empty(blobStore.HeaderReadsFor);
        Assert.Empty(indexStore.Upserts);
        Assert.Empty(trackingStore.MarkedComplete);
    }

    [Fact]
    public async Task HandleAsync_propagates_required_files_provider_exception_unchanged_and_makes_no_further_writes()
    {
        var trackingStore = new FakeSnapshotTrackingStore
        {
            StatusToReturn = SnapshotTrackingStatus.Receiving,
            ReceivedFilesToReturn = ["header.json", "instruments.json", "calculations.json", "settings.json"],
        };
        var requiredFilesProvider = new FakeRequiredFilesProvider(); // "portfolio" deliberately unconfigured
        var indexStore = new FakeSnapshotIndexStore();
        var blobStore = new FakeSnapshotBlobStore();

        var handler = CreateHandler(blobStore, trackingStore, requiredFilesProvider, indexStore);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => handler.HandleAsync(CreateMessage(), CancellationToken.None));

        Assert.Empty(blobStore.HeaderReadsFor);
        Assert.Empty(indexStore.Upserts);
        Assert.Empty(trackingStore.MarkedComplete);
    }

    private static SnapshotMessageHandler CreateHandler(
        FakeSnapshotBlobStore blobStore,
        FakeSnapshotTrackingStore trackingStore,
        FakeRequiredFilesProvider? requiredFilesProvider = null,
        FakeSnapshotIndexStore? indexStore = null,
        CapturingLogger<SnapshotMessageHandler>? logger = null)
    {
        if (requiredFilesProvider is null)
        {
            requiredFilesProvider = new FakeRequiredFilesProvider();
            requiredFilesProvider.RequiredFilesByType["portfolio"] = PortfolioRequiredFiles;
        }

        return new SnapshotMessageHandler(
            blobStore,
            trackingStore,
            requiredFilesProvider,
            indexStore ?? new FakeSnapshotIndexStore(),
            logger ?? new CapturingLogger<SnapshotMessageHandler>());
    }

    private static SnapshotMessage CreateMessage(string payloadType = "instruments", string payload = """{"total":21}""")
    {
        using var payloadDocument = JsonDocument.Parse(payload);
        return new SnapshotMessage
        {
            SnapshotId = "corr98765",
            AccountId = "00675442A",
            SnapshotType = "portfolio",
            PayloadType = payloadType,
            Stage = "PreTrade",
            PublishedAt = new DateTime(2026, 5, 22, 6, 10, 14, DateTimeKind.Utc),
            PublishedBy = "PortfolioCalculation",
            SchemaVersion = "1.0",
            Payload = payloadDocument.RootElement.Clone(),
        };
    }
}
