using System.Globalization;
using Microsoft.EntityFrameworkCore;
using UBS.AM.PLT.Snapshot.Application.Exceptions;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql;

namespace UBS.AM.PLT.Snapshot.IntegrationTests;

/// <summary>
/// Malformed / unexpected input handling, run against REAL Azure resources
/// (ADLS Gen2 + Azure SQL) with Kafka bypassed. These cover inputs that deserialise into a
/// valid envelope but violate a downstream expectation:
/// TC-24 an unknown snapshotType (no SnapshotConfig entry); TC-26 a header whose
/// <c>Payload.Event</c> is null (the header is opaque JSON text now, never deserialised into a
/// DTO — a null/absent/unusable <c>Payload.Event</c> is a non-retryable upstream contract
/// breach, so the existing tracking row becomes FAILED and no index row is written; see
/// <c>PortfolioSnapshotIndexEntryBuilder.ExtractHeaderValues</c>).
/// Both are open design questions and are intentionally kept here as xUnit tests rather
/// than converted to Gherkin.
/// TC-25 (an extra payloadType not in the required-files set) is covered by the Gherkin
/// scenarios in <c>Features/UnexpectedPayloadHandling.feature</c>.
/// TC-23 (missing / null required envelope field) is covered by the Application-layer
/// null-identity guard unit tests in <c>SnapshotMessageHandlerTests</c>, because "no blob /
/// no tracking / no commit" is asserted most rigorously against fakes. The envelope
/// deserialisation half sits in the Kafka adapter: a message that will not deserialise never
/// reaches the command, so nothing is written and the adapter owns its offset handling.
/// </summary>
public sealed class MalformedInputTests : IntegrationTestBase, IClassFixture<SnapshotFixture>
{
    // Fresh account id — IT-ACC-001..007 are used by Groups A/B/C/F/G.
    private const string AccountId = "IT-ACC-008";

    private const string OrdersJson = """{"positions":[{"isin":"CH0038863350","qty":250}]}""";
    private const string PortfolioJson = """{"nav":5555.55,"ccy":"CHF"}""";
    private const string SettingsJson = """{"tolerance":0.05}""";

    // Header with an explicit null Payload.Event. The opaque header remains stored verbatim,
    // but its snapshot is rejected before an index row can be written.
    private const string NullNestedEventHeaderJson = """
        {
          "SnapshotId": "corr20260714-0001",
          "Type": "Header",
          "Payload": {
            "Event": null,
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
        var message = CreateMessage(snapshotId, "orders", OrdersJson, snapshotType: "mystery");

        var exception = await Record.ExceptionAsync(
            () => Fixture.Handler.HandleAsync(message, CancellationToken.None));

        // Completeness check could not resolve the required-files list for "mystery".
        Assert.IsType<KeyNotFoundException>(exception);

        // Blob and tracking row were written before the failing completeness step.
        var tracking = await GetTrackingAsync(snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Receiving, tracking.Status);
        Assert.Equal(new[] { "orders.json" }, tracking.ReceivedFiles);
        Assert.Null(tracking.CompletedAt);
        Assert.StartsWith("mystery_snapshots/", tracking.AdlsRootPath);
        Assert.True(
            await Fixture.BlobContainer.GetBlobClient($"{tracking.AdlsRootPath}/orders.json").ExistsAsync(),
            "Expected the orders blob to have been written before the completeness check failed.");

        // Never reaches the index.
        Assert.False(await IndexRowExistsAsync(snapshotId));
    }

    [Fact]
    public async Task Header_with_null_Payload_Event_rejects_when_a_later_payload_completes_the_snapshot()
    {
        // TC-26: header payloads are opaque, but the one required filter value is mandatory.
        // It is deliberately delivered first; settings is the completing message, proving the
        // rejection applies to an existing row rather than only to a malformed final header.
        Fixture.CurrentTime = new DateTimeOffset(2026, 7, 14, 20, 0, 0, TimeSpan.Zero);
        var snapshotId = NewSnapshotId("tc26");

        await Fixture.Handler.HandleAsync(
            CreateMessage(snapshotId, "header", NullNestedEventHeaderJson),
            CancellationToken.None);
        await Fixture.Handler.HandleAsync(CreateMessage(snapshotId, "orders", OrdersJson), CancellationToken.None);
        await Fixture.Handler.HandleAsync(CreateMessage(snapshotId, "portfolio", PortfolioJson), CancellationToken.None);

        var thrown = await Assert.ThrowsAsync<InvalidSnapshotHeaderException>(
            () => Fixture.Handler.HandleAsync(
                CreateMessage(snapshotId, "settings", SettingsJson),
                CancellationToken.None));
        Assert.Equal(InvalidSnapshotHeaderException.InvalidHeaderEventReason, thrown.ReasonCode);

        var tracking = await GetTrackingAsync(snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Failed, tracking.Status);
        Assert.Contains(InvalidSnapshotHeaderException.InvalidHeaderEventReason, tracking.Reason);
        Assert.NotNull(tracking.DeclaredFailedAt);
        Assert.Null(tracking.CompletedAt);
        Assert.Equal(4, tracking.ReceivedFiles.Count);

        Assert.False(await IndexRowExistsAsync(snapshotId));

        var notification = Assert.Single(
            Fixture.ResponsePublisher.PublishedFor(snapshotId),
            entry => entry.Status == SnapshotTrackingStatus.Failed);
        Assert.Equal(InvalidSnapshotHeaderException.InvalidHeaderEventReason, notification.ReasonCode);
        Assert.Equal(thrown.Message, notification.ReasonDetail);
    }

    private SnapshotMessage CreateMessage(
        string snapshotId,
        string payloadType,
        string payloadJson,
        string snapshotType = "portfolio")
        => new()
        {
            SnapshotId = snapshotId,
            AccountId = AccountId,
            SnapshotType = snapshotType,
            PayloadType = payloadType,
            PublishedAt = Fixture.CurrentTime.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            PublishedBy = "PortfolioCalculation",
            Payload = payloadJson,
        };

    private async Task<SnapshotTrackingEntity> GetTrackingAsync(string snapshotId)
    {
        await using var context = await Fixture.DbContextFactory.CreateDbContextAsync();
        return await context.SnapshotTracking
            .AsNoTracking()
            .SingleAsync(e => e.SnapshotId == snapshotId);
    }

    private async Task<PortfolioSnapshotIndexEntity> GetIndexAsync(string snapshotId)
    {
        await using var context = await Fixture.DbContextFactory.CreateDbContextAsync();
        return await context.PortfolioSnapshotIndex
            .AsNoTracking()
            .SingleAsync(e => e.SnapshotId == snapshotId);
    }

    private async Task<bool> IndexRowExistsAsync(string snapshotId)
    {
        await using var context = await Fixture.DbContextFactory.CreateDbContextAsync();
        return await context.PortfolioSnapshotIndex
            .AsNoTracking()
            .AnyAsync(e => e.SnapshotId == snapshotId);
    }
}
