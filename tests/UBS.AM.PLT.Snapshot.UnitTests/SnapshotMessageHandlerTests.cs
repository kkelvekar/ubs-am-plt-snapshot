using System.Text.Json;
using Microsoft.Extensions.Logging;
using UBS.AM.PLT.Snapshot.Application.Exceptions;
using UBS.AM.PLT.Snapshot.Application.Features.SnapshotIngestion;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;
using UBS.AM.PLT.Snapshot.UnitTests.Fakes;
using Xunit;

namespace UBS.AM.PLT.Snapshot.UnitTests;

public class SnapshotMessageHandlerTests
{
    private static readonly HashSet<string> PortfolioRequiredFiles =
        ["header.json", "orders.json", "calculations.json", "settings.json"];

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
        var orders = CreateMessage(payloadType: "orders");

        await handler.HandleAsync(header, CancellationToken.None);
        timeProvider.UtcNow = new DateTimeOffset(2026, 6, 1, 0, 0, 2, TimeSpan.Zero); // month boundary crossed
        await handler.HandleAsync(orders, CancellationToken.None);

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
    public async Task HandleAsync_logs_snapshotId_accountId_and_payloadType()
    {
        var logger = new CapturingLogger<SnapshotMessageHandler>();
        var handler = CreateHandler(new FakeSnapshotBlobStore(), new FakeSnapshotTrackingStore(), logger: logger);

        await handler.HandleAsync(CreateMessage(), CancellationToken.None);

        // The per-payload line specifically — the same message also emits the receiving
        // response line, since this is the snapshot's first payload.
        var entry = Assert.Single(logger.Entries, e => e.Message.StartsWith("Wrote snapshot payload blob", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("corr98765", entry.State["SnapshotId"]);
        Assert.Equal("00675442A", entry.State["AccountId"]);
        Assert.Equal("orders", entry.State["PayloadType"]);
    }

    [Fact]
    public async Task HandleAsync_does_not_touch_index_or_mark_complete_when_receiving_and_incomplete()
    {
        var trackingStore = new FakeSnapshotTrackingStore
        {
            StatusToReturn = SnapshotTrackingStatus.Receiving,
            ReceivedFilesToReturn = ["header.json", "orders.json"],
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
            ReceivedFilesToReturn = ["header.json", "orders.json", "calculations.json", "settings.json"],
            CallOrderLog = callOrderLog,
        };
        var requiredFilesProvider = new FakeRequiredFilesProvider();
        requiredFilesProvider.RequiredFilesByType["portfolio"] = PortfolioRequiredFiles;
        var indexStore = new FakeSnapshotIndexStore { CallOrderLog = callOrderLog };
        var responsePublisher = new FakeSnapshotResponsePublisher { CallOrderLog = callOrderLog };

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
        var handler = CreateHandler(
            blobStore,
            trackingStore,
            requiredFilesProvider,
            indexStore,
            timeProvider: timeProvider,
            responsePublisher: responsePublisher);

        await handler.HandleAsync(message, CancellationToken.None);

        var expectedRootPath = SnapshotBlobPath.RootFolder(message, timeProvider.UtcNow);
        Assert.Equal([expectedRootPath], blobStore.HeaderReadsFor);

        var indexEntry = Assert.Single(indexStore.Upserts);
        Assert.Equal(message.SnapshotId, indexEntry.SnapshotId);
        Assert.Equal(message.AccountId, indexEntry.AccountId);
        Assert.Equal(new DateTime(2026, 5, 22, 6, 10, 14, DateTimeKind.Utc), indexEntry.SnapshotDate);
        Assert.Equal("ModelChange", indexEntry.EventType);
        Assert.Equal(expectedRootPath, indexEntry.AdlsPath);
        Assert.Equal(headerJson, indexEntry.DisplayData);

        Assert.Equal([message.SnapshotId], trackingStore.MarkedComplete);

        // Both the index write and the response publish must precede the status flip: once
        // the row reads COMPLETE the completeness branch stops firing, so anything after the
        // flip could never be retried by a redelivery.
        Assert.Equal(
            [
                nameof(FakeSnapshotIndexStore.UpsertAsync),
                nameof(FakeSnapshotResponsePublisher.PublishAsync),
                nameof(FakeSnapshotTrackingStore.MarkCompleteAsync),
            ],
            callOrderLog);
    }

    [Fact]
    public async Task HandleAsync_publishes_a_completion_response_carrying_the_tracking_state()
    {
        var trackingStore = new FakeSnapshotTrackingStore
        {
            StatusToReturn = SnapshotTrackingStatus.Receiving,
            ReceivedFilesToReturn = ["header.json", "orders.json", "calculations.json", "settings.json"],
        };
        var requiredFilesProvider = new FakeRequiredFilesProvider();
        requiredFilesProvider.RequiredFilesByType["portfolio"] = PortfolioRequiredFiles;
        var blobStore = new FakeSnapshotBlobStore { HeaderJson = """{"eventType":"ModelChange"}""" };
        var responsePublisher = new FakeSnapshotResponsePublisher();
        var timeProvider = new RecordingTimeProvider();
        var message = CreateMessage(payloadType: "settings");

        var handler = CreateHandler(
            blobStore,
            trackingStore,
            requiredFilesProvider,
            timeProvider: timeProvider,
            responsePublisher: responsePublisher);

        await handler.HandleAsync(message, CancellationToken.None);

        var notification = Assert.Single(responsePublisher.Published);
        Assert.Equal(message.SnapshotId, notification.SnapshotId);
        Assert.Equal(message.AccountId, notification.AccountId);
        Assert.Equal(SnapshotTrackingStatus.Complete, notification.Status);
        Assert.Equal(
            ["header.json", "orders.json", "calculations.json", "settings.json"],
            notification.ReceivedFiles);
        Assert.Empty(notification.MissingFiles);
        Assert.Equal(new DateTime(2026, 5, 22, 6, 10, 14, DateTimeKind.Utc), notification.FirstReceivedAt);
        Assert.Equal(new DateTime(2026, 5, 22, 6, 10, 14, DateTimeKind.Utc), notification.LastUpdatedAt);
        Assert.Equal(timeProvider.UtcNow.UtcDateTime, notification.CompletedAt);
        Assert.Null(notification.DeclaredFailedAt);
    }

    [Fact]
    public async Task HandleAsync_publishes_a_receiving_response_for_the_snapshots_first_payload()
    {
        var trackingStore = new FakeSnapshotTrackingStore
        {
            StatusToReturn = SnapshotTrackingStatus.Receiving,
            ReceivedFilesToReturn = ["orders.json"],
        };
        var requiredFilesProvider = new FakeRequiredFilesProvider();
        requiredFilesProvider.RequiredFilesByType["portfolio"] = PortfolioRequiredFiles;
        var responsePublisher = new FakeSnapshotResponsePublisher();
        var message = CreateMessage();

        var handler = CreateHandler(
            new FakeSnapshotBlobStore(),
            trackingStore,
            requiredFilesProvider,
            responsePublisher: responsePublisher);

        await handler.HandleAsync(message, CancellationToken.None);

        var notification = Assert.Single(responsePublisher.Published);
        Assert.Equal(message.SnapshotId, notification.SnapshotId);
        Assert.Equal(message.AccountId, notification.AccountId);
        Assert.Equal(SnapshotTrackingStatus.Receiving, notification.Status);
        Assert.Equal(["orders.json"], notification.ReceivedFiles);

        // Sorted, so the same snapshot state always renders the same wire value.
        Assert.Equal(["calculations.json", "header.json", "settings.json"], notification.MissingFiles);
        Assert.Null(notification.CompletedAt);
        Assert.Null(notification.DeclaredFailedAt);
    }

    [Fact]
    public async Task HandleAsync_publishes_no_response_for_a_later_payload_that_does_not_complete()
    {
        // Second of four: the snapshot was already announced on its first payload, and it is
        // not complete yet, so this message says nothing.
        var trackingStore = new FakeSnapshotTrackingStore
        {
            StatusToReturn = SnapshotTrackingStatus.Receiving,
            ReceivedFilesToReturn = ["header.json", "orders.json"],
        };
        var requiredFilesProvider = new FakeRequiredFilesProvider();
        requiredFilesProvider.RequiredFilesByType["portfolio"] = PortfolioRequiredFiles;
        var responsePublisher = new FakeSnapshotResponsePublisher();

        var handler = CreateHandler(
            new FakeSnapshotBlobStore(),
            trackingStore,
            requiredFilesProvider,
            responsePublisher: responsePublisher);

        await handler.HandleAsync(CreateMessage(), CancellationToken.None);

        Assert.Empty(responsePublisher.Published);
    }

    [Fact]
    public async Task HandleAsync_publishes_only_completion_when_a_single_required_file_both_starts_and_completes()
    {
        // A snapshot type requiring one file: that payload is both the first and the
        // completing one, and must produce exactly one response — Complete, not Receiving.
        var trackingStore = new FakeSnapshotTrackingStore
        {
            StatusToReturn = SnapshotTrackingStatus.Receiving,
            ReceivedFilesToReturn = ["header.json"],
        };
        var requiredFilesProvider = new FakeRequiredFilesProvider();
        requiredFilesProvider.RequiredFilesByType["portfolio"] = new HashSet<string> { "header.json" };
        var responsePublisher = new FakeSnapshotResponsePublisher();

        var handler = CreateHandler(
            new FakeSnapshotBlobStore { HeaderJson = """{"eventType":"ModelChange"}""" },
            trackingStore,
            requiredFilesProvider,
            responsePublisher: responsePublisher);

        await handler.HandleAsync(CreateMessage(payloadType: "header"), CancellationToken.None);

        var notification = Assert.Single(responsePublisher.Published);
        Assert.Equal(SnapshotTrackingStatus.Complete, notification.Status);
        Assert.Empty(notification.MissingFiles);
    }

    [Fact]
    public async Task HandleAsync_publishes_no_second_response_when_tracking_already_complete()
    {
        // Stray redelivery after completion: the publisher was already told, and the
        // completeness branch must stay skipped.
        var trackingStore = new FakeSnapshotTrackingStore { StatusToReturn = SnapshotTrackingStatus.Complete };
        trackingStore.RootPathsBySnapshotId["corr98765"] =
            "portfolio_snapshots/year=2026/month=04/accountId=00675442A/snapshotId=corr98765";
        var responsePublisher = new FakeSnapshotResponsePublisher();

        var handler = CreateHandler(
            new FakeSnapshotBlobStore(),
            trackingStore,
            responsePublisher: responsePublisher);

        await handler.HandleAsync(CreateMessage(), CancellationToken.None);

        Assert.Empty(responsePublisher.Published);
    }

    [Fact]
    public async Task HandleAsync_propagates_publisher_exception_unchanged_and_leaves_tracking_receiving()
    {
        // No offset commit and no status flip, so the redelivery re-enters the completeness
        // branch and re-publishes: the notification is at-least-once, never lost.
        var trackingStore = new FakeSnapshotTrackingStore
        {
            StatusToReturn = SnapshotTrackingStatus.Receiving,
            ReceivedFilesToReturn = ["header.json", "orders.json", "calculations.json", "settings.json"],
        };
        var requiredFilesProvider = new FakeRequiredFilesProvider();
        requiredFilesProvider.RequiredFilesByType["portfolio"] = PortfolioRequiredFiles;
        var boom = new InvalidOperationException("broker unreachable");
        var responsePublisher = new FakeSnapshotResponsePublisher { ThrowOnPublish = boom };

        var handler = CreateHandler(
            new FakeSnapshotBlobStore { HeaderJson = """{"eventType":"ModelChange"}""" },
            trackingStore,
            requiredFilesProvider,
            responsePublisher: responsePublisher);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(CreateMessage(), CancellationToken.None));

        Assert.Same(boom, thrown);
        Assert.Empty(trackingStore.MarkedComplete);
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

        // One lookup only — the pre-write payload-type check. The completeness branch is
        // skipped entirely, so the list is never resolved a second time for it.
        Assert.Single(requiredFilesProvider.Calls);
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
            ReceivedFilesToReturn = ["header.json", "orders.json", "calculations.json", "settings.json"],
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
    [InlineData("Payload")]
    public async Task HandleAsync_rejects_a_null_required_envelope_field_before_any_write(string nullField)
    {
        // TC-23b: a present-but-null envelope field passes JSON `required` deserialisation
        // but would corrupt the blob path / tracking row (or write an empty .json blob).
        // The handler must reject it before the first (blob) write, throwing a rejection so
        // the consumer commits past it instead of redelivering it forever.
        var blobStore = new FakeSnapshotBlobStore();
        var trackingStore = new FakeSnapshotTrackingStore();
        var logger = new CapturingLogger<SnapshotMessageHandler>();
        var handler = CreateHandler(blobStore, trackingStore, logger: logger);
        var message = CreateMessageWithNullField(nullField);

        var thrown = await Assert.ThrowsAsync<InvalidSnapshotEnvelopeException>(
            () => handler.HandleAsync(message, CancellationToken.None));

        var expectedReason = nullField == "Payload"
            ? InvalidSnapshotEnvelopeException.EmptyPayloadReason
            : InvalidSnapshotEnvelopeException.NullRequiredFieldReason;
        Assert.Equal(expectedReason, thrown.ReasonCode);
        Assert.Contains(nullField == "Payload" ? "Payload" : nullField, thrown.Message, StringComparison.Ordinal);

        Assert.Empty(blobStore.Written);
        Assert.Empty(trackingStore.Upserts);

        // The rejection IS recorded against the tracking row — unless the null field is the
        // snapshotId itself, which is the row's primary key and so has nothing to record against.
        if (nullField == "SnapshotId")
        {
            Assert.Empty(trackingStore.MarkedRejected);
        }
        else
        {
            Assert.Single(trackingStore.MarkedRejected);
        }

        // Only the rejection-response line: no payload was written, so no per-payload line.
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.StartsWith("Published snapshot rejection response", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_rejects_a_payload_type_outside_the_snapshot_types_file_contract()
    {
        // Expected payloads are exactly the required files, so an out-of-contract payload is
        // refused before it can reach blob storage or received_files.
        var blobStore = new FakeSnapshotBlobStore();
        var trackingStore = new FakeSnapshotTrackingStore();
        var indexStore = new FakeSnapshotIndexStore();
        var logger = new CapturingLogger<SnapshotMessageHandler>();
        var handler = CreateHandler(blobStore, trackingStore, indexStore: indexStore, logger: logger);

        var thrown = await Assert.ThrowsAsync<InvalidSnapshotEnvelopeException>(
            () => handler.HandleAsync(CreateMessage(payloadType: "auditlog"), CancellationToken.None));

        Assert.Equal(InvalidSnapshotEnvelopeException.UnexpectedPayloadTypeReason, thrown.ReasonCode);
        Assert.Contains("auditlog", thrown.Message, StringComparison.Ordinal);

        // The producer needs to know what it should have sent.
        Assert.Contains("header.json", thrown.Message, StringComparison.Ordinal);

        Assert.Empty(blobStore.Written);
        Assert.Empty(trackingStore.Upserts);
        Assert.Empty(indexStore.Upserts);
        Assert.Single(trackingStore.MarkedRejected);
        Assert.Single(logger.Entries, entry => entry.Message.StartsWith("Published snapshot rejection response", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleAsync_does_not_reject_a_payload_type_when_the_snapshot_type_is_unknown()
    {
        // No expected-file list exists to check against, and it is not the publisher's fault,
        // so this keeps its existing behaviour: blob and tracking are written and the
        // completeness step then fails to resolve the list.
        var blobStore = new FakeSnapshotBlobStore();
        var trackingStore = new FakeSnapshotTrackingStore();
        var requiredFilesProvider = new FakeRequiredFilesProvider(); // "portfolio" deliberately unconfigured

        var handler = CreateHandler(blobStore, trackingStore, requiredFilesProvider);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => handler.HandleAsync(CreateMessage(payloadType: "auditlog"), CancellationToken.None));

        Assert.Single(blobStore.Written);
        Assert.Single(trackingStore.Upserts);
    }

    [Fact]
    public async Task HandleAsync_rejections_are_catchable_as_the_non_retryable_category()
    {
        // The consumer branches on the base type, not on the concrete reason — that is what
        // keeps a future non-retryable case working without touching the consumer.
        var handler = CreateHandler(new FakeSnapshotBlobStore(), new FakeSnapshotTrackingStore());

        await Assert.ThrowsAnyAsync<SnapshotMessageRejectedException>(
            () => handler.HandleAsync(CreateMessageWithNullField("SnapshotId"), CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("""{"total":21""")]
    [InlineData("not json at all")]
    [InlineData("""{"total":21}{"total":22}""")]
    public async Task HandleAsync_rejects_a_syntactically_invalid_payload_before_touching_any_store(string payload)
    {
        // The payload now arrives as a string of already-serialised JSON. A syntactically
        // broken one would land in blob as an invalid .json file, so the handler rejects it
        // up front — blob, tracking and index must all be untouched, and nothing logged.
        var blobStore = new FakeSnapshotBlobStore();
        var trackingStore = new FakeSnapshotTrackingStore();
        var indexStore = new FakeSnapshotIndexStore();
        var logger = new CapturingLogger<SnapshotMessageHandler>();
        var handler = CreateHandler(blobStore, trackingStore, indexStore: indexStore, logger: logger);

        var thrown = await Assert.ThrowsAsync<InvalidSnapshotEnvelopeException>(
            () => handler.HandleAsync(CreateMessage(payload: payload), CancellationToken.None));

        // Blank payloads are caught by the emptiness guard; the rest fail the JSON parse and
        // carry the underlying JsonException as the inner exception.
        if (string.IsNullOrWhiteSpace(payload))
        {
            Assert.Equal(InvalidSnapshotEnvelopeException.EmptyPayloadReason, thrown.ReasonCode);
        }
        else
        {
            Assert.Equal(InvalidSnapshotEnvelopeException.MalformedPayloadJsonReason, thrown.ReasonCode);
            Assert.IsAssignableFrom<JsonException>(thrown.InnerException);
        }

        Assert.Empty(blobStore.Written);
        Assert.Empty(blobStore.HeaderReadsFor);
        Assert.Empty(trackingStore.Upserts);
        Assert.Empty(trackingStore.MarkedComplete);
        Assert.Empty(indexStore.Upserts);
        Assert.Single(trackingStore.MarkedRejected);
        Assert.Single(logger.Entries, entry => entry.Message.StartsWith("Published snapshot rejection response", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleAsync_hands_the_blob_store_the_payload_text_verbatim_on_every_delivery()
    {
        // Byte-identity is what makes the blob overwrite content-idempotent: the handler
        // must never re-serialise the payload from its syntax-check parse. Insignificant
        // whitespace and key order are therefore preserved exactly, on the first delivery
        // and on redelivery.
        const string payload = """{  "total" : 21,  "b":1, "a":2  }""";
        var blobStore = new FakeSnapshotBlobStore();
        var handler = CreateHandler(blobStore, new FakeSnapshotTrackingStore());

        await handler.HandleAsync(CreateMessage(payload: payload), CancellationToken.None);
        await handler.HandleAsync(CreateMessage(payload: payload), CancellationToken.None);

        Assert.Equal(2, blobStore.Written.Count);
        Assert.All(blobStore.Written, written => Assert.Equal(payload, written.Message.Payload));
    }

    [Fact]
    public async Task HandleAsync_persists_the_header_blob_text_byte_for_byte_as_display_data()
    {
        // DisplayData must be the header.json blob content verbatim — never re-serialised.
        // Odd whitespace, key order and producer casing therefore survive untouched.
        const string headerJson = """{  "EventType" : "ModelChange",   "zzz":1,  "aaa" : 2  }""";
        var (handler, indexStore, _) = CreateCompletingHandler(headerJson);

        await handler.HandleAsync(CreateMessage(payloadType: "header"), CancellationToken.None);

        var indexEntry = Assert.Single(indexStore.Upserts);
        Assert.Equal(headerJson, indexEntry.DisplayData);
    }

    [Fact]
    public async Task HandleAsync_extracts_eventType_from_a_camelCase_header()
    {
        var (handler, indexStore, logger) = CreateCompletingHandler("""{"eventType":"ModelChange"}""");

        await handler.HandleAsync(CreateMessage(payloadType: "header"), CancellationToken.None);

        Assert.Equal("ModelChange", Assert.Single(indexStore.Upserts).EventType);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task HandleAsync_extracts_EventType_from_a_PascalCase_header()
    {
        // Regression guard: the old JsonSerializerDefaults.Web binding was case-insensitive.
        // JsonDocument property lookup is not, so PascalCase headers must be matched explicitly.
        var (handler, indexStore, logger) = CreateCompletingHandler("""{"EventType":"ModelChange"}""");

        await handler.HandleAsync(CreateMessage(payloadType: "header"), CancellationToken.None);

        Assert.Equal("ModelChange", Assert.Single(indexStore.Upserts).EventType);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task HandleAsync_takes_the_first_eventType_in_document_order_when_the_header_repeats_it()
    {
        // JSON permits duplicate keys, and case-insensitive matching widens what counts as a
        // duplicate. First match in document order wins — the only rule that keeps a
        // redelivery of the same bytes producing the same row.
        var (handler, indexStore, _) =
            CreateCompletingHandler("""{"eventType":"First","EventType":"Second","eventtype":"Third"}""");

        await handler.HandleAsync(CreateMessage(payloadType: "header"), CancellationToken.None);

        Assert.Equal("First", Assert.Single(indexStore.Upserts).EventType);
    }

    [Fact]
    public async Task HandleAsync_does_not_fall_through_to_a_later_eventType_when_the_first_is_unusable()
    {
        // Same rule, unhappy path: an unusable first match is NOT skipped in favour of a
        // usable later one. Scanning on would make the result depend on how many duplicates
        // the producer happened to send.
        var (handler, indexStore, logger) =
            CreateCompletingHandler("""{"eventType":42,"EventType":"ModelChange"}""");

        await handler.HandleAsync(CreateMessage(payloadType: "header"), CancellationToken.None);

        Assert.Equal(string.Empty, Assert.Single(indexStore.Upserts).EventType);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Theory]
    [InlineData("""{"benchmark":"MCCHM2EQ"}""")]                 // eventType absent entirely
    [InlineData("""{"eventType":null}""")]                       // JSON null
    [InlineData("""{"eventType":42}""")]                         // number, not a string
    [InlineData("""{"eventType":{"code":"ModelChange"}}""")]     // object, not a string
    [InlineData("""{"eventType":true}""")]                       // bool, not a string
    [InlineData("""{"eventType":""}""")]                         // present but empty
    [InlineData("""["eventType","ModelChange"]""")]              // root is not an object
    public async Task HandleAsync_writes_the_index_row_with_an_empty_eventType_and_warns_when_the_header_has_none(
        string headerJson)
    {
        // An upstream contract breach, not a transport failure: retrying forever would never
        // fix it, so the row is still written (with the header text intact) and ops get a WARN.
        var (handler, indexStore, logger) = CreateCompletingHandler(headerJson);

        await handler.HandleAsync(CreateMessage(payloadType: "header"), CancellationToken.None);

        var indexEntry = Assert.Single(indexStore.Upserts);
        Assert.Equal(string.Empty, indexEntry.EventType);
        Assert.Equal(headerJson, indexEntry.DisplayData);

        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal("corr98765", warning.State["SnapshotId"]);
        Assert.Equal("00675442A", warning.State["AccountId"]);
        Assert.Equal("header", warning.State["PayloadType"]);
    }

    [Fact]
    public async Task HandleAsync_carries_header_fields_unknown_to_this_service_into_display_data()
    {
        // THE point of making the header opaque: a producer can add a field that exists
        // nowhere in this codebase and it reaches the audit UI with no code change here.
        const string headerJson =
            """{"eventType":"ModelChange","aFieldNoCSharpTypeHasEverHeardOf":"survives","nested":{"deep":[1,2,3]}}""";
        var (handler, indexStore, _) = CreateCompletingHandler(headerJson);

        await handler.HandleAsync(CreateMessage(payloadType: "header"), CancellationToken.None);

        var indexEntry = Assert.Single(indexStore.Upserts);
        Assert.Equal(headerJson, indexEntry.DisplayData);
        Assert.Contains("aFieldNoCSharpTypeHasEverHeardOf", indexEntry.DisplayData);
        Assert.Contains("\"deep\":[1,2,3]", indexEntry.DisplayData);
    }

    [Fact]
    public async Task HandleAsync_carries_portfolioStatus_and_orderStatus_into_display_data()
    {
        // Both were parsed and then silently dropped by the old typed display-data mapping.
        const string headerJson =
            """{"eventType":"ModelChange","portfolioStatus":"ReadyToSend","orderStatus":"Sent"}""";
        var (handler, indexStore, _) = CreateCompletingHandler(headerJson);

        await handler.HandleAsync(CreateMessage(payloadType: "header"), CancellationToken.None);

        var indexEntry = Assert.Single(indexStore.Upserts);
        Assert.Equal(headerJson, indexEntry.DisplayData);
        Assert.Contains("\"portfolioStatus\":\"ReadyToSend\"", indexEntry.DisplayData);
        Assert.Contains("\"orderStatus\":\"Sent\"", indexEntry.DisplayData);
    }

    [Theory]
    [InlineData("""{"eventType":"ModelChange" """)]
    [InlineData("not json at all")]
    [InlineData("")]
    public async Task HandleAsync_throws_and_writes_no_index_row_when_the_header_blob_is_malformed(string headerJson)
    {
        // Recovery is forward: no index row, no MarkComplete, so the consumer never commits
        // and a corrected header redelivered later still completes the snapshot.
        var (handler, indexStore, trackingStore) = CreateCompletingHandlerWithTracking(headerJson);

        await Assert.ThrowsAnyAsync<JsonException>(
            () => handler.HandleAsync(CreateMessage(payloadType: "header"), CancellationToken.None));

        Assert.Empty(indexStore.Upserts);
        Assert.Empty(trackingStore.MarkedComplete);
    }

    [Theory]
    [InlineData("SnapshotId", SnapshotFieldLimits.SnapshotIdMaxLength)]
    [InlineData("AccountId", SnapshotFieldLimits.AccountIdMaxLength)]
    [InlineData("SnapshotType", SnapshotFieldLimits.SnapshotTypeMaxLength)]
    [InlineData("PayloadType", SnapshotFieldLimits.PayloadTypeMaxLength)]
    public async Task HandleAsync_accepts_an_identity_field_at_exactly_its_maximum_length(string field, int maxLength)
    {
        var message = CreateMessageWithFieldValue(field, new string('a', maxLength));
        var blobStore = new FakeSnapshotBlobStore();
        var trackingStore = new FakeSnapshotTrackingStore();
        var requiredFilesProvider = new FakeRequiredFilesProvider();
        requiredFilesProvider.RequiredFilesByType[message.SnapshotType] =
            new HashSet<string>(PortfolioRequiredFiles) { SnapshotBlobPath.FileName(message.PayloadType) };
        var handler = CreateHandler(blobStore, trackingStore, requiredFilesProvider);

        await handler.HandleAsync(message, CancellationToken.None);

        Assert.Single(blobStore.Written);
    }

    [Theory]
    [InlineData("SnapshotId", SnapshotFieldLimits.SnapshotIdMaxLength)]
    [InlineData("AccountId", SnapshotFieldLimits.AccountIdMaxLength)]
    [InlineData("SnapshotType", SnapshotFieldLimits.SnapshotTypeMaxLength)]
    [InlineData("PayloadType", SnapshotFieldLimits.PayloadTypeMaxLength)]
    public async Task HandleAsync_rejects_an_identity_field_one_character_over_its_maximum_length(
        string field,
        int maxLength)
    {
        // A value too long for its column would fail the SQL write with a truncation error on
        // every redelivery forever. Bounded here instead, before the first write, so it is a
        // rejection the consumer commits past — and every post-write failure stays retryable.
        var blobStore = new FakeSnapshotBlobStore();
        var trackingStore = new FakeSnapshotTrackingStore();
        var indexStore = new FakeSnapshotIndexStore();
        var logger = new CapturingLogger<SnapshotMessageHandler>();
        var handler = CreateHandler(blobStore, trackingStore, indexStore: indexStore, logger: logger);
        var message = CreateMessageWithFieldValue(field, new string('a', maxLength + 1));

        var thrown = await Assert.ThrowsAsync<InvalidSnapshotEnvelopeException>(
            () => handler.HandleAsync(message, CancellationToken.None));

        Assert.Equal(InvalidSnapshotEnvelopeException.FieldTooLongReason, thrown.ReasonCode);
        Assert.Contains(field, thrown.Message, StringComparison.Ordinal);

        Assert.Empty(blobStore.Written);
        Assert.Empty(trackingStore.Upserts);
        Assert.Empty(indexStore.Upserts);

        // An over-long snapshotId does not fit its primary-key column, so that one rejection
        // cannot be recorded; the rest are.
        Assert.Equal(field == "SnapshotId" ? 0 : 1, trackingStore.MarkedRejected.Count);
        Assert.Single(logger.Entries, entry => entry.Message.StartsWith("Published snapshot rejection response", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("SnapshotId", "corr/98765")]        // path separator
    [InlineData("SnapshotId", "corr 98765")]        // space
    [InlineData("SnapshotId", "..")]                // traversal
    [InlineData("SnapshotId", "corré98765")]   // non-ASCII
    [InlineData("AccountId", "0067/5442A")]
    [InlineData("AccountId", "0067 5442A")]
    [InlineData("AccountId", "../00675442A")]
    [InlineData("AccountId", "00675442Å")]
    [InlineData("SnapshotType", "port/folio")]
    [InlineData("SnapshotType", "port folio")]
    [InlineData("SnapshotType", "..")]
    [InlineData("SnapshotType", "portfölio")]
    [InlineData("PayloadType", "or/ders")]
    [InlineData("PayloadType", "or ders")]
    [InlineData("PayloadType", "../orders")]
    [InlineData("PayloadType", "ordèrs")]
    public async Task HandleAsync_rejects_a_path_forming_field_carrying_unusable_characters(string field, string value)
    {
        // All four identity fields compose the blob path (see SnapshotBlobPath), so a
        // separator, a traversal sequence or an exotic character would put the blob somewhere
        // other than its snapshot folder — refused before the first write.
        var blobStore = new FakeSnapshotBlobStore();
        var trackingStore = new FakeSnapshotTrackingStore();
        var indexStore = new FakeSnapshotIndexStore();
        var logger = new CapturingLogger<SnapshotMessageHandler>();
        var handler = CreateHandler(blobStore, trackingStore, indexStore: indexStore, logger: logger);

        var thrown = await Assert.ThrowsAsync<InvalidSnapshotEnvelopeException>(
            () => handler.HandleAsync(CreateMessageWithFieldValue(field, value), CancellationToken.None));

        Assert.Equal(InvalidSnapshotEnvelopeException.InvalidFieldCharactersReason, thrown.ReasonCode);
        Assert.Contains(field, thrown.Message, StringComparison.Ordinal);

        Assert.Empty(blobStore.Written);
        Assert.Empty(trackingStore.Upserts);
        Assert.Empty(indexStore.Upserts);

        // A snapshotId with unusable characters still FITS its column, so every one of these
        // rejections is recorded — only the blob path could not have been formed from it.
        Assert.Single(trackingStore.MarkedRejected);
        Assert.Single(logger.Entries, entry => entry.Message.StartsWith("Published snapshot rejection response", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleAsync_records_and_publishes_a_rejection_before_rethrowing_it()
    {
        // The producer is told why its message was refused, and the refusal is visible in SQL
        // as a FAILED row — while blob and index stay untouched and the consumer still sees
        // the rejection so it commits past the message.
        var callOrderLog = new List<string>();
        var blobStore = new FakeSnapshotBlobStore();
        var trackingStore = new FakeSnapshotTrackingStore { CallOrderLog = callOrderLog };
        var indexStore = new FakeSnapshotIndexStore();
        var responsePublisher = new FakeSnapshotResponsePublisher { CallOrderLog = callOrderLog };
        var handler = CreateHandler(
            blobStore,
            trackingStore,
            indexStore: indexStore,
            responsePublisher: responsePublisher);

        var thrown = await Assert.ThrowsAsync<InvalidSnapshotEnvelopeException>(
            () => handler.HandleAsync(CreateMessage(payloadType: "auditlog"), CancellationToken.None));

        var rejection = Assert.Single(trackingStore.MarkedRejected);
        Assert.Equal("corr98765", rejection.SnapshotId);
        Assert.Equal("00675442A", rejection.AccountId);
        Assert.Equal("portfolio", rejection.SnapshotType);
        Assert.Equal(InvalidSnapshotEnvelopeException.UnexpectedPayloadTypeReason, rejection.ReasonCode);
        Assert.Equal(thrown.Message, rejection.ReasonDetail);

        var notification = Assert.Single(responsePublisher.Published);
        Assert.Equal("corr98765", notification.SnapshotId);
        Assert.Equal("00675442A", notification.AccountId);
        Assert.Equal(SnapshotTrackingStatus.Failed, notification.Status);
        Assert.Equal(InvalidSnapshotEnvelopeException.UnexpectedPayloadTypeReason, notification.ReasonCode);
        Assert.Equal(thrown.Message, notification.ReasonDetail);
        Assert.NotNull(notification.DeclaredFailedAt);
        Assert.Null(notification.CompletedAt);
        Assert.Empty(notification.MissingFiles);

        // Record first, publish second: a failed recording must never leave the rejection
        // reported-but-unrecorded.
        Assert.Equal(
            [
                nameof(FakeSnapshotTrackingStore.MarkRejectedAsync),
                nameof(FakeSnapshotResponsePublisher.PublishAsync),
            ],
            callOrderLog);

        Assert.Empty(blobStore.Written);
        Assert.Empty(indexStore.Upserts);
    }

    [Fact]
    public async Task HandleAsync_publishes_a_rejection_carrying_the_recorded_rows_state()
    {
        // A snapshot already part-received when a bad message arrives: the response reports
        // the files that DID arrive, read back from the row the rejection wrote.
        var trackingStore = new FakeSnapshotTrackingStore
        {
            RejectedRowToReturn = new SnapshotTrackingEntity
            {
                SnapshotId = "corr98765",
                AccountId = "00675442A",
                SnapshotType = "portfolio",
                AdlsRootPath = "portfolio_snapshots/year=2026/month=05/accountId=00675442A/snapshotId=corr98765",
                ReceivedFiles = ["header.json", "orders.json"],
                Status = SnapshotTrackingStatus.Failed,
                Reason = "UNEXPECTED_PAYLOAD_TYPE: whatever",
                FirstReceivedAt = new DateTime(2026, 5, 22, 6, 0, 0, DateTimeKind.Utc),
                LastUpdatedAt = new DateTime(2026, 5, 22, 6, 5, 0, DateTimeKind.Utc),
                DeclaredFailedAt = new DateTime(2026, 5, 22, 6, 5, 0, DateTimeKind.Utc),
            },
        };
        var responsePublisher = new FakeSnapshotResponsePublisher();
        var handler = CreateHandler(
            new FakeSnapshotBlobStore(),
            trackingStore,
            responsePublisher: responsePublisher);

        await Assert.ThrowsAsync<InvalidSnapshotEnvelopeException>(
            () => handler.HandleAsync(CreateMessage(payloadType: "auditlog"), CancellationToken.None));

        var notification = Assert.Single(responsePublisher.Published);
        Assert.Equal(["header.json", "orders.json"], notification.ReceivedFiles);
        Assert.Equal(new DateTime(2026, 5, 22, 6, 0, 0, DateTimeKind.Utc), notification.FirstReceivedAt);
        Assert.Equal(new DateTime(2026, 5, 22, 6, 5, 0, DateTimeKind.Utc), notification.LastUpdatedAt);
        Assert.Equal(new DateTime(2026, 5, 22, 6, 5, 0, DateTimeKind.Utc), notification.DeclaredFailedAt);
    }

    [Fact]
    public async Task HandleAsync_publishes_a_rejection_without_recording_it_when_the_snapshotId_is_unstorable()
    {
        // The snapshotId is the tracking primary key, so an over-long one has nothing to
        // record against — but the producer is still told, with its own raw value echoed back
        // so it can tell which message this is about.
        var overlong = new string('a', SnapshotFieldLimits.SnapshotIdMaxLength + 1);
        var trackingStore = new FakeSnapshotTrackingStore();
        var responsePublisher = new FakeSnapshotResponsePublisher();
        var timeProvider = new RecordingTimeProvider();
        var handler = CreateHandler(
            new FakeSnapshotBlobStore(),
            trackingStore,
            timeProvider: timeProvider,
            responsePublisher: responsePublisher);

        await Assert.ThrowsAsync<InvalidSnapshotEnvelopeException>(
            () => handler.HandleAsync(CreateMessage(snapshotId: overlong), CancellationToken.None));

        Assert.Empty(trackingStore.MarkedRejected);

        var notification = Assert.Single(responsePublisher.Published);
        Assert.Equal(overlong, notification.SnapshotId);
        Assert.Equal("00675442A", notification.AccountId);
        Assert.Equal(SnapshotTrackingStatus.Failed, notification.Status);
        Assert.Equal(InvalidSnapshotEnvelopeException.FieldTooLongReason, notification.ReasonCode);
        Assert.Empty(notification.ReceivedFiles);
        Assert.Equal(timeProvider.UtcNow.UtcDateTime, notification.FirstReceivedAt);
        Assert.Equal(timeProvider.UtcNow.UtcDateTime, notification.LastUpdatedAt);
        Assert.Equal(timeProvider.UtcNow.UtcDateTime, notification.DeclaredFailedAt);
    }

    [Fact]
    public async Task HandleAsync_publishes_a_rejection_without_recording_it_when_the_snapshotId_is_null()
    {
        var trackingStore = new FakeSnapshotTrackingStore();
        var responsePublisher = new FakeSnapshotResponsePublisher();
        var handler = CreateHandler(
            new FakeSnapshotBlobStore(),
            trackingStore,
            responsePublisher: responsePublisher);

        await Assert.ThrowsAsync<InvalidSnapshotEnvelopeException>(
            () => handler.HandleAsync(CreateMessageWithNullField("SnapshotId"), CancellationToken.None));

        Assert.Empty(trackingStore.MarkedRejected);

        var notification = Assert.Single(responsePublisher.Published);
        Assert.Equal(string.Empty, notification.SnapshotId);
        Assert.Equal(SnapshotTrackingStatus.Failed, notification.Status);
        Assert.Equal(InvalidSnapshotEnvelopeException.NullRequiredFieldReason, notification.ReasonCode);
    }

    [Fact]
    public async Task HandleAsync_propagates_a_publisher_failure_during_rejection_reporting_unchanged()
    {
        // Not swallowed and not converted into the rejection: the offset is then never
        // committed, the retry ladder runs, and redelivery re-reports the rejection.
        var boom = new InvalidOperationException("broker unreachable");
        var trackingStore = new FakeSnapshotTrackingStore();
        var responsePublisher = new FakeSnapshotResponsePublisher { ThrowOnPublish = boom };
        var handler = CreateHandler(
            new FakeSnapshotBlobStore(),
            trackingStore,
            responsePublisher: responsePublisher);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(CreateMessage(payloadType: "auditlog"), CancellationToken.None));

        Assert.Same(boom, thrown);
        Assert.Single(trackingStore.MarkedRejected);
    }

    [Fact]
    public async Task HandleAsync_propagates_a_tracking_failure_during_rejection_recording_and_publishes_nothing()
    {
        // Recording comes first precisely so this failure is loud: the offset is not
        // committed and redelivery retries both steps, rather than the rejection being
        // reported to the producer and then silently lost from SQL.
        var boom = new InvalidOperationException("sql unavailable");
        var trackingStore = new FakeSnapshotTrackingStore { ThrowOnMarkRejected = boom };
        var responsePublisher = new FakeSnapshotResponsePublisher();
        var handler = CreateHandler(
            new FakeSnapshotBlobStore(),
            trackingStore,
            responsePublisher: responsePublisher);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(CreateMessage(payloadType: "auditlog"), CancellationToken.None));

        Assert.Same(boom, thrown);
        Assert.Empty(responsePublisher.Published);
    }

    [Fact]
    public void ExtractEventType_returns_empty_for_a_value_longer_than_the_EventType_column()
    {
        var overlong = new string('x', SnapshotFieldLimits.EventTypeMaxLength + 1);

        Assert.Equal(
            string.Empty,
            SnapshotIndexEntryBuilder.ExtractEventType($$"""{"eventType":"{{overlong}}"}"""));
    }

    [Fact]
    public void ExtractEventType_returns_a_value_of_exactly_the_EventType_column_length()
    {
        var atLimit = new string('x', SnapshotFieldLimits.EventTypeMaxLength);

        Assert.Equal(
            atLimit,
            SnapshotIndexEntryBuilder.ExtractEventType($$"""{"eventType":"{{atLimit}}"}"""));
    }

    [Fact]
    public async Task HandleAsync_writes_an_empty_eventType_and_warns_when_the_header_eventType_is_too_long()
    {
        // The over-long value reuses the EXISTING "no usable eventType" path: the index row is
        // still written (retrying could never fix an upstream contract breach), the header text
        // still reaches display_data verbatim, and ops get the same WARN.
        var overlong = new string('x', SnapshotFieldLimits.EventTypeMaxLength + 1);
        var headerJson = $$"""{"eventType":"{{overlong}}"}""";
        var (handler, indexStore, logger) = CreateCompletingHandler(headerJson);

        await handler.HandleAsync(CreateMessage(payloadType: "header"), CancellationToken.None);

        var indexEntry = Assert.Single(indexStore.Upserts);
        Assert.Equal(string.Empty, indexEntry.EventType);
        Assert.Equal(headerJson, indexEntry.DisplayData);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    private static SnapshotMessage CreateMessageWithFieldValue(string field, string value)
        => CreateMessage(
            payloadType: field == "PayloadType" ? value : "orders",
            snapshotId: field == "SnapshotId" ? value : "corr98765",
            accountId: field == "AccountId" ? value : "00675442A",
            snapshotType: field == "SnapshotType" ? value : "portfolio");

    private static (SnapshotMessageHandler Handler, FakeSnapshotIndexStore IndexStore, CapturingLogger<SnapshotMessageHandler> Logger)
        CreateCompletingHandler(string headerJson)
    {
        var (handler, indexStore, _, logger) = CreateCompletingParts(headerJson);
        return (handler, indexStore, logger);
    }

    private static (SnapshotMessageHandler Handler, FakeSnapshotIndexStore IndexStore, FakeSnapshotTrackingStore TrackingStore)
        CreateCompletingHandlerWithTracking(string headerJson)
    {
        var (handler, indexStore, trackingStore, _) = CreateCompletingParts(headerJson);
        return (handler, indexStore, trackingStore);
    }

    /// <summary>
    /// A handler whose tracking store reports every required file already received, so the
    /// next delivery completes the snapshot and builds the index entry from
    /// <paramref name="headerJson"/>.
    /// </summary>
    private static (SnapshotMessageHandler Handler, FakeSnapshotIndexStore IndexStore, FakeSnapshotTrackingStore TrackingStore, CapturingLogger<SnapshotMessageHandler> Logger)
        CreateCompletingParts(string headerJson)
    {
        var trackingStore = new FakeSnapshotTrackingStore
        {
            StatusToReturn = SnapshotTrackingStatus.Receiving,
            ReceivedFilesToReturn = ["header.json", "orders.json", "calculations.json", "settings.json"],
        };
        var requiredFilesProvider = new FakeRequiredFilesProvider();
        requiredFilesProvider.RequiredFilesByType["portfolio"] = PortfolioRequiredFiles;
        var indexStore = new FakeSnapshotIndexStore();
        var logger = new CapturingLogger<SnapshotMessageHandler>();
        var handler = CreateHandler(
            new FakeSnapshotBlobStore { HeaderJson = headerJson },
            trackingStore,
            requiredFilesProvider,
            indexStore,
            logger: logger);

        return (handler, indexStore, trackingStore, logger);
    }

    private static SnapshotMessage CreateMessageWithNullField(string nullField)
        => new()
        {
            SnapshotId = nullField == "SnapshotId" ? null! : "corr98765",
            AccountId = nullField == "AccountId" ? null! : "00675442A",
            SnapshotType = nullField == "SnapshotType" ? null! : "portfolio",
            PayloadType = nullField == "PayloadType" ? null! : "orders",
            PublishedAt = "2026-05-22T06:10:14Z",
            PublishedBy = "PortfolioCalculation",
            Payload = nullField == "Payload" ? null! : """{"total":21}""",
        };

    private static SnapshotMessageHandler CreateHandler(
        FakeSnapshotBlobStore blobStore,
        FakeSnapshotTrackingStore trackingStore,
        FakeRequiredFilesProvider? requiredFilesProvider = null,
        FakeSnapshotIndexStore? indexStore = null,
        TimeProvider? timeProvider = null,
        CapturingLogger<SnapshotMessageHandler>? logger = null,
        FakeSnapshotResponsePublisher? responsePublisher = null)
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
            responsePublisher ?? new FakeSnapshotResponsePublisher(),
            timeProvider ?? new RecordingTimeProvider(),
            logger ?? new CapturingLogger<SnapshotMessageHandler>());
    }

    private static SnapshotMessage CreateMessage(
        string payloadType = "orders",
        string payload = """{"total":21}""",
        string snapshotId = "corr98765",
        string accountId = "00675442A",
        string snapshotType = "portfolio")
        => new()
        {
            SnapshotId = snapshotId,
            AccountId = accountId,
            SnapshotType = snapshotType,
            PayloadType = payloadType,
            PublishedAt = "2026-05-22T06:10:14Z",
            PublishedBy = "PortfolioCalculation",
            Payload = payload,
        };
}
