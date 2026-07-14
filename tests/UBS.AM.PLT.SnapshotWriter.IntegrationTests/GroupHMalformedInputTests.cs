using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UBS.AM.PLT.SnapshotWriter.Domain;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence;

namespace UBS.AM.PLT.SnapshotWriter.IntegrationTests;

/// <summary>
/// Group H — malformed / unexpected input (TC-24..TC-26), run against REAL Azure resources
/// (ADLS Gen2 + Azure SQL) with Kafka bypassed. These cover inputs that deserialise into a
/// valid envelope but violate a downstream expectation:
/// TC-24 an unknown snapshotType (no SnapshotConfig entry); TC-25 an extra payloadType that
/// is not in the required-files set (opacity: stored, never blocks completion); TC-26 a
/// header whose <c>eventType</c> is null (rejected by the index's NOT NULL column, then
/// recovered forward by a corrected header).
/// TC-23 (missing / null required envelope field) is covered as unit tests — the
/// deserialisation guard in <c>KafkaSnapshotConsumerTests</c> and the Application-layer
/// null-identity guard in <c>SnapshotMessageHandlerTests</c> — because "no blob / no
/// tracking / no commit" is asserted most rigorously against fakes.
/// </summary>
public sealed class GroupHMalformedInputTests : IntegrationTestBase, IClassFixture<SnapshotWriterFixture>
{
    // Fresh account id — IT-ACC-001..007 are used by Groups A/B/C/F/G.
    private const string AccountId = "IT-ACC-008";

    private const string InstrumentsJson = """{"positions":[{"isin":"CH0038863350","qty":250}]}""";
    private const string CalculationsJson = """{"nav":5555.55,"ccy":"CHF"}""";
    private const string SettingsJson = """{"tolerance":0.05}""";
    private const string ExtraPayloadJson = """{"entries":[{"at":"2026-07-14T10:00:00Z","by":"system"}]}""";

    // Header with an explicit null eventType: passes System.Text.Json's `required` check
    // (the property is present), so it reaches the index write where event_type NOT NULL
    // rejects it. All other required header fields are present and valid.
    private const string NullEventTypeHeaderJson = """
        {
          "eventType": null,
          "portfolioStatus": "APPROVED",
          "orderStatus": "SENT",
          "benchmark": "MSCI World",
          "baseCcy": "CHF",
          "programId": "PRG-7",
          "batchId": "BATCH-2026-07-14",
          "numOrders": 17,
          "ptcAlerts": 2,
          "orderApprovedBy": "approver@ubs.com",
          "orderApprovedAt": "2026-07-14T10:45:00Z",
          "orderSentBy": "sender@ubs.com",
          "orderSentAt": "2026-07-14T10:50:00Z"
        }
        """;

    public GroupHMalformedInputTests(SnapshotWriterFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task Unknown_snapshotType_writes_blob_and_tracking_but_never_completes()
    {
        // TC-24: snapshotType has no SnapshotConfig entry. Per design (decision: no pre-write
        // config check), the blob and tracking row are written first; the completeness step
        // then throws when the required-files list cannot be resolved, so the message fails
        // and retries — no index row, snapshot never COMPLETE.
        Fixture.CurrentTime = new DateTimeOffset(2026, 7, 14, 17, 0, 0, TimeSpan.Zero);
        var snapshotId = NewSnapshotId("tc24");
        var message = CreateMessage(snapshotId, "instruments", InstrumentsJson, snapshotType: "mystery");

        var exception = await Record.ExceptionAsync(
            () => Fixture.Handler.HandleAsync(message, CancellationToken.None));

        // Completeness check could not resolve the required-files list for "mystery".
        Assert.IsType<KeyNotFoundException>(exception);

        // Blob and tracking row were written before the failing completeness step.
        var tracking = await GetTrackingAsync(snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Receiving, tracking.Status);
        Assert.Equal(new[] { "instruments.json" }, tracking.ReceivedFiles);
        Assert.Null(tracking.CompletedAt);
        Assert.StartsWith("mystery_snapshots/", tracking.AdlsRootPath);
        Assert.True(
            await Fixture.BlobContainer.GetBlobClient($"{tracking.AdlsRootPath}/instruments.json").ExistsAsync(),
            "Expected the instruments blob to have been written before the completeness check failed.");

        // Never reaches the index.
        Assert.False(await IndexRowExistsAsync(snapshotId));
    }

    [Fact]
    public async Task Extra_payload_arriving_before_required_files_is_stored_and_does_not_block_completion()
    {
        // TC-25 (extra-before): an unconfigured payloadType (auditlog.json) arrives first.
        // It is stored opaquely and recorded in received_files, but completeness is
        // required ⊆ received, so the four required files still drive the snapshot COMPLETE.
        Fixture.CurrentTime = new DateTimeOffset(2026, 7, 14, 18, 0, 0, TimeSpan.Zero);
        var snapshotId = NewSnapshotId("tc25a");

        await Fixture.Handler.HandleAsync(CreateMessage(snapshotId, "auditlog", ExtraPayloadJson), CancellationToken.None);

        var afterExtra = await GetTrackingAsync(snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Receiving, afterExtra.Status);
        Assert.Contains("auditlog.json", afterExtra.ReceivedFiles);
        Assert.False(await IndexRowExistsAsync(snapshotId));

        await Fixture.Handler.HandleAsync(CreateMessage(snapshotId, "instruments", InstrumentsJson), CancellationToken.None);
        await Fixture.Handler.HandleAsync(CreateMessage(snapshotId, "calculations", CalculationsJson), CancellationToken.None);
        await Fixture.Handler.HandleAsync(CreateMessage(snapshotId, "settings", SettingsJson), CancellationToken.None);
        await Fixture.Handler.HandleAsync(CreateMessage(snapshotId, "header", TestPayloads.HeaderJson), CancellationToken.None);

        var tracking = await GetTrackingAsync(snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Complete, tracking.Status);
        Assert.NotNull(tracking.CompletedAt);
        // The extra file is retained in received_files alongside the four required ones.
        Assert.Contains("auditlog.json", tracking.ReceivedFiles);
        Assert.Equal(5, tracking.ReceivedFiles.Count);

        // The extra blob was stored opaquely (opacity invariant).
        Assert.True(
            await Fixture.BlobContainer.GetBlobClient($"{tracking.AdlsRootPath}/auditlog.json").ExistsAsync(),
            "Expected the extra auditlog blob to have been stored.");

        Assert.True(await IndexRowExistsAsync(snapshotId));
    }

    [Fact]
    public async Task Extra_payload_arriving_after_completion_is_stored_without_re_completing()
    {
        // TC-25 (extra-after): the snapshot is already COMPLETE when an unconfigured
        // payloadType arrives. It is stored and unioned into received_files, but the
        // completeness/index step is skipped (status no longer RECEIVING) — no duplicate or
        // re-written index row.
        Fixture.CurrentTime = new DateTimeOffset(2026, 7, 14, 19, 0, 0, TimeSpan.Zero);
        var snapshotId = NewSnapshotId("tc25b");

        await Fixture.Handler.HandleAsync(CreateMessage(snapshotId, "instruments", InstrumentsJson), CancellationToken.None);
        await Fixture.Handler.HandleAsync(CreateMessage(snapshotId, "calculations", CalculationsJson), CancellationToken.None);
        await Fixture.Handler.HandleAsync(CreateMessage(snapshotId, "settings", SettingsJson), CancellationToken.None);
        await Fixture.Handler.HandleAsync(CreateMessage(snapshotId, "header", TestPayloads.HeaderJson), CancellationToken.None);

        var trackingBefore = await GetTrackingAsync(snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Complete, trackingBefore.Status);
        var indexBefore = await GetIndexAsync(snapshotId);

        Fixture.CurrentTime = Fixture.CurrentTime.AddMinutes(5);
        await Fixture.Handler.HandleAsync(CreateMessage(snapshotId, "auditlog", ExtraPayloadJson), CancellationToken.None);

        var trackingAfter = await GetTrackingAsync(snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Complete, trackingAfter.Status);
        Assert.Equal(trackingBefore.CompletedAt, trackingAfter.CompletedAt);
        Assert.Contains("auditlog.json", trackingAfter.ReceivedFiles);
        Assert.Equal(5, trackingAfter.ReceivedFiles.Count);

        Assert.True(
            await Fixture.BlobContainer.GetBlobClient($"{trackingAfter.AdlsRootPath}/auditlog.json").ExistsAsync(),
            "Expected the extra auditlog blob to have been stored after completion.");

        // Exactly one index row, unchanged — the extra file did not re-trigger the index UPSERT.
        await using (var context = await Fixture.DbContextFactory.CreateDbContextAsync())
        {
            Assert.Equal(1, await context.SnapshotIndex.AsNoTracking().CountAsync(e => e.SnapshotId == snapshotId));
        }

        var indexAfter = await GetIndexAsync(snapshotId);
        Assert.Equal(indexBefore.CreatedAt, indexAfter.CreatedAt);
        Assert.Equal(indexBefore.EventType, indexAfter.EventType);
    }

    [Fact]
    public async Task Header_with_null_eventType_fails_the_index_write_then_recovers_forward()
    {
        // TC-26: the completing header carries a null eventType. It passes deserialisation
        // but the index write is rejected (event_type NOT NULL), so the message fails and
        // retries — no index row, tracking stays RECEIVING. A later corrected header
        // completes the snapshot (forward recovery), proving no schema change or skip path
        // is needed.
        Fixture.CurrentTime = new DateTimeOffset(2026, 7, 14, 20, 0, 0, TimeSpan.Zero);
        var snapshotId = NewSnapshotId("tc26");

        await Fixture.Handler.HandleAsync(CreateMessage(snapshotId, "instruments", InstrumentsJson), CancellationToken.None);
        await Fixture.Handler.HandleAsync(CreateMessage(snapshotId, "calculations", CalculationsJson), CancellationToken.None);
        await Fixture.Handler.HandleAsync(CreateMessage(snapshotId, "settings", SettingsJson), CancellationToken.None);

        // Completing header with null eventType — the index write must reject it.
        var badHeader = CreateMessage(snapshotId, "header", NullEventTypeHeaderJson);
        var exception = await Record.ExceptionAsync(
            () => Fixture.Handler.HandleAsync(badHeader, CancellationToken.None));
        Assert.NotNull(exception);

        // No index row; tracking stays RECEIVING with all four files present.
        Assert.False(await IndexRowExistsAsync(snapshotId));
        var afterBadHeader = await GetTrackingAsync(snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Receiving, afterBadHeader.Status);
        Assert.Null(afterBadHeader.CompletedAt);
        Assert.Equal(4, afterBadHeader.ReceivedFiles.Count);

        // Forward recovery: a corrected header redelivered later completes the snapshot.
        Fixture.CurrentTime = Fixture.CurrentTime.AddMinutes(3);
        await Fixture.Handler.HandleAsync(
            CreateMessage(snapshotId, "header", TestPayloads.HeaderJson), CancellationToken.None);

        var tracking = await GetTrackingAsync(snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Complete, tracking.Status);
        Assert.NotNull(tracking.CompletedAt);

        var index = await GetIndexAsync(snapshotId);
        Assert.Equal(TestPayloads.ExpectedHeader.EventType, index.EventType);
    }

    private SnapshotMessage CreateMessage(
        string snapshotId,
        string payloadType,
        string payloadJson,
        string snapshotType = "portfolio")
    {
        using var document = JsonDocument.Parse(payloadJson);

        return new SnapshotMessage
        {
            SnapshotId = snapshotId,
            AccountId = AccountId,
            SnapshotType = snapshotType,
            PayloadType = payloadType,
            Stage = "PreTrade",
            PublishedAt = Fixture.CurrentTime.UtcDateTime,
            PublishedBy = "PortfolioCalculation",
            SchemaVersion = "1.0",
            Payload = document.RootElement.Clone(),
        };
    }

    private async Task<SnapshotTrackingEntry> GetTrackingAsync(string snapshotId)
    {
        await using var context = await Fixture.DbContextFactory.CreateDbContextAsync();
        return await context.SnapshotTracking
            .AsNoTracking()
            .SingleAsync(e => e.SnapshotId == snapshotId);
    }

    private async Task<SnapshotIndexEntry> GetIndexAsync(string snapshotId)
    {
        await using var context = await Fixture.DbContextFactory.CreateDbContextAsync();
        return await context.SnapshotIndex
            .AsNoTracking()
            .SingleAsync(e => e.SnapshotId == snapshotId);
    }

    private async Task<bool> IndexRowExistsAsync(string snapshotId)
    {
        await using var context = await Fixture.DbContextFactory.CreateDbContextAsync();
        return await context.SnapshotIndex
            .AsNoTracking()
            .AnyAsync(e => e.SnapshotId == snapshotId);
    }
}
