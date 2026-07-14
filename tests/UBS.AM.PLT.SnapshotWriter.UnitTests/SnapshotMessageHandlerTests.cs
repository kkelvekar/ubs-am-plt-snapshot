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
        Assert.Same(message, written.Message);
    }

    [Fact]
    public async Task HandleAsync_writes_blob_and_upserts_tracking_with_the_root_path_pinned_at_arrival_time()
    {
        var timeProvider = new RecordingTimeProvider();
        var blobStore = new FakeSnapshotBlobStore();
        var trackingStore = new FakeSnapshotTrackingStore();
        var handler = CreateHandler(blobStore, trackingStore, timeProvider: timeProvider);
        var message = CreateMessage();

        await handler.HandleAsync(message, CancellationToken.None);

        var expectedRootPath = SnapshotBlobPath.RootFolder(message, timeProvider.UtcNow);
        var written = Assert.Single(blobStore.Written);
        Assert.Equal(expectedRootPath, written.RootPath);

        var upsert = Assert.Single(trackingStore.Upserts);
        Assert.Same(message, upsert.Message);
        Assert.Equal(expectedRootPath, upsert.AdlsRootPath);
    }

    [Fact]
    public async Task HandleAsync_reuses_the_pinned_root_path_for_a_later_payload_arriving_in_a_different_month()
    {
        var timeProvider = new RecordingTimeProvider { UtcNow = new DateTimeOffset(2026, 5, 31, 23, 59, 58, TimeSpan.Zero) };
        var blobStore = new FakeSnapshotBlobStore();
        var trackingStore = new FakeSnapshotTrackingStore();
        var handler = CreateHandler(blobStore, trackingStore, timeProvider: timeProvider);

        var header = CreateMessage(payloadType: "header");
        var instruments = CreateMessage(payloadType: "instruments");

        await handler.HandleAsync(header, CancellationToken.None);
        timeProvider.UtcNow = new DateTimeOffset(2026, 6, 1, 0, 0, 2, TimeSpan.Zero); // month boundary crossed
        await handler.HandleAsync(instruments, CancellationToken.None);

        var pinnedRootPath = SnapshotBlobPath.RootFolder(header, new DateTimeOffset(2026, 5, 31, 23, 59, 58, TimeSpan.Zero));
        Assert.Contains("month=05", pinnedRootPath);

        Assert.Equal(2, blobStore.Written.Count);
        Assert.All(blobStore.Written, written => Assert.Equal(pinnedRootPath, written.RootPath));
        Assert.Equal(2, trackingStore.Upserts.Count);
        Assert.All(trackingStore.Upserts, upsert => Assert.Equal(pinnedRootPath, upsert.AdlsRootPath));
        Assert.Equal(pinnedRootPath, trackingStore.RootPathsBySnapshotId[header.SnapshotId]);
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
    public async Task HandleAsync_logs_snapshotId_accountId_payloadType_and_stage()
    {
        var logger = new CapturingLogger<SnapshotMessageHandler>();
        var handler = CreateHandler(new FakeSnapshotBlobStore(), new FakeSnapshotTrackingStore(), logger: logger);

        await handler.HandleAsync(CreateMessage(), CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("corr98765", entry.State["SnapshotId"]);
        Assert.Equal("00675442A", entry.State["AccountId"]);
        Assert.Equal("instruments", entry.State["PayloadType"]);
        Assert.Equal("PreTrade", entry.State["Stage"]);
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

        var timeProvider = new RecordingTimeProvider();
        var handler = CreateHandler(blobStore, trackingStore, requiredFilesProvider, indexStore, timeProvider: timeProvider);

        await handler.HandleAsync(message, CancellationToken.None);

        var expectedRootPath = SnapshotBlobPath.RootFolder(message, timeProvider.UtcNow);
        Assert.Equal([expectedRootPath], blobStore.HeaderReadsFor);

        var indexEntry = Assert.Single(indexStore.Upserts);
        Assert.Equal(message.SnapshotId, indexEntry.SnapshotId);
        Assert.Equal(message.AccountId, indexEntry.AccountId);
        Assert.Equal(new DateTime(2026, 5, 22, 6, 10, 14, DateTimeKind.Utc), indexEntry.SnapshotDate);
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
        // Redelivery long after completion: the root pinned by the snapshot's first
        // payload (a different month than "now") must still be reused for the blob write.
        const string pinnedRootPath = "portfolio_snapshots/year=2026/month=04/accountId=00675442A/snapshotId=corr98765";
        var trackingStore = new FakeSnapshotTrackingStore { StatusToReturn = SnapshotTrackingStatus.Complete };
        trackingStore.RootPathsBySnapshotId["corr98765"] = pinnedRootPath;
        var requiredFilesProvider = new FakeRequiredFilesProvider();
        requiredFilesProvider.RequiredFilesByType["portfolio"] = PortfolioRequiredFiles;
        var indexStore = new FakeSnapshotIndexStore();
        var blobStore = new FakeSnapshotBlobStore();

        var handler = CreateHandler(blobStore, trackingStore, requiredFilesProvider, indexStore);

        await handler.HandleAsync(CreateMessage(), CancellationToken.None);

        var written = Assert.Single(blobStore.Written);
        Assert.Equal(pinnedRootPath, written.RootPath);
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

    [Theory]
    [InlineData("SnapshotId")]
    [InlineData("AccountId")]
    [InlineData("SnapshotType")]
    [InlineData("PayloadType")]
    public async Task HandleAsync_rejects_a_null_required_identity_field_before_any_write(string nullField)
    {
        // TC-23b: a present-but-null identity field passes JSON `required` deserialisation
        // but would corrupt the blob path / tracking row. The handler must reject it before
        // the first (blob) write, throwing so the consumer's retry/alert path handles it.
        var blobStore = new FakeSnapshotBlobStore();
        var trackingStore = new FakeSnapshotTrackingStore();
        var logger = new CapturingLogger<SnapshotMessageHandler>();
        var handler = CreateHandler(blobStore, trackingStore, logger: logger);
        var message = CreateMessageWithNullField(nullField);

        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.HandleAsync(message, CancellationToken.None));

        Assert.Empty(blobStore.Written);
        Assert.Empty(trackingStore.Upserts);
        Assert.Empty(logger.Entries);
    }

    private static SnapshotMessage CreateMessageWithNullField(string nullField)
    {
        using var payloadDocument = JsonDocument.Parse("""{"total":21}""");
        return new SnapshotMessage
        {
            SnapshotId = nullField == "SnapshotId" ? null! : "corr98765",
            AccountId = nullField == "AccountId" ? null! : "00675442A",
            SnapshotType = nullField == "SnapshotType" ? null! : "portfolio",
            PayloadType = nullField == "PayloadType" ? null! : "instruments",
            Stage = "PreTrade",
            PublishedAt = new DateTime(2026, 5, 22, 6, 10, 14, DateTimeKind.Utc),
            PublishedBy = "PortfolioCalculation",
            SchemaVersion = "1.0",
            Payload = payloadDocument.RootElement.Clone(),
        };
    }

    private static SnapshotMessageHandler CreateHandler(
        FakeSnapshotBlobStore blobStore,
        FakeSnapshotTrackingStore trackingStore,
        FakeRequiredFilesProvider? requiredFilesProvider = null,
        FakeSnapshotIndexStore? indexStore = null,
        TimeProvider? timeProvider = null,
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
            timeProvider ?? new RecordingTimeProvider(),
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
