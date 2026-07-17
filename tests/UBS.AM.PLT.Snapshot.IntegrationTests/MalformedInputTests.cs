using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql;

namespace UBS.AM.PLT.Snapshot.IntegrationTests;

/// <summary>
/// Malformed / unexpected input handling, run against REAL Azure resources
/// (ADLS Gen2 + Azure SQL) with Kafka bypassed. These cover inputs that deserialise into a
/// valid envelope but violate a downstream expectation:
/// TC-24 an unknown snapshotType (no SnapshotConfig entry); TC-26 a header whose
/// <c>eventType</c> is null (rejected by the index's NOT NULL column, then recovered forward
/// by a corrected header). Both are open design questions and are intentionally kept here as
/// xUnit tests rather than converted to Gherkin.
/// TC-25 (an extra payloadType not in the required-files set) is covered by the Gherkin
/// scenarios in <c>Features/UnexpectedPayloadHandling.feature</c>.
/// TC-23 (missing / null required envelope field) is covered as unit tests — the
/// deserialisation guard in <c>KafkaSnapshotConsumerTests</c> and the Application-layer
/// null-identity guard in <c>SnapshotMessageHandlerTests</c> — because "no blob / no
/// tracking / no commit" is asserted most rigorously against fakes.
/// </summary>
public sealed class MalformedInputTests : IntegrationTestBase, IClassFixture<SnapshotFixture>
{
    // Fresh account id — IT-ACC-001..007 are used by Groups A/B/C/F/G.
    private const string AccountId = "IT-ACC-008";

    private const string InstrumentsJson = """{"positions":[{"isin":"CH0038863350","qty":250}]}""";
    private const string CalculationsJson = """{"nav":5555.55,"ccy":"CHF"}""";
    private const string SettingsJson = """{"tolerance":0.05}""";

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

    public MalformedInputTests(SnapshotFixture fixture)
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

    private async Task<SnapshotTrackingEntity> GetTrackingAsync(string snapshotId)
    {
        await using var context = await Fixture.DbContextFactory.CreateDbContextAsync();
        return await context.SnapshotTracking
            .AsNoTracking()
            .SingleAsync(e => e.SnapshotId == snapshotId);
    }

    private async Task<SnapshotIndexEntity> GetIndexAsync(string snapshotId)
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
