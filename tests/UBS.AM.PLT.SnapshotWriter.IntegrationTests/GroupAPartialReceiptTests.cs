using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UBS.AM.PLT.SnapshotWriter.Domain;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence;

namespace UBS.AM.PLT.SnapshotWriter.IntegrationTests;

/// <summary>
/// Group A — partial receipt scenarios (TC-01..TC-03), run against REAL Azure resources
/// (ADLS Gen2 + Azure SQL) with Kafka bypassed: every message goes straight through the
/// real <c>ISnapshotMessageHandler</c> resolved from the production DI graph. Only
/// <c>TimeProvider</c> is mocked, so arrival timestamps (and the year/month blob path
/// segments derived from them) are deterministic.
/// </summary>
public sealed class GroupAPartialReceiptTests : IntegrationTestBase, IClassFixture<SnapshotWriterFixture>
{
    private const string AccountId = "IT-ACC-001";

    public GroupAPartialReceiptTests(SnapshotWriterFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task First_payload_for_new_snapshot_inserts_receiving_row_with_no_index()
    {
        var startTime = new DateTimeOffset(2026, 7, 13, 9, 15, 0, TimeSpan.Zero);
        Fixture.CurrentTime = startTime;
        var snapshotId = NewSnapshotId("tc01");
        var message = CreateMessage(snapshotId, "instruments", """{"positions":[{"isin":"CH0012032048","qty":100}]}""");

        await Fixture.Handler.HandleAsync(message, CancellationToken.None);

        var expectedRootPath =
            $"portfolio_snapshots/year={startTime:yyyy}/month={startTime:MM}/accountId={AccountId}/snapshotId={snapshotId}";
        var blobText = await DownloadBlobTextAsync($"{expectedRootPath}/instruments.json");
        Assert.Equal(message.Payload.GetRawText(), blobText);

        var tracking = await GetTrackingAsync(snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Receiving, tracking.Status);
        Assert.Equal(new[] { "instruments.json" }, tracking.ReceivedFiles);
        Assert.Equal(expectedRootPath, tracking.AdlsRootPath);
        Assert.Equal(startTime.UtcDateTime, tracking.FirstReceivedAt);
        Assert.Equal(startTime.UtcDateTime, tracking.LastUpdatedAt);

        Assert.False(await IndexRowExistsAsync(snapshotId));
    }

    [Fact]
    public async Task Second_nonfinal_payload_for_existing_snapshot_updates_tracking_without_completing()
    {
        var startTime = new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero);
        Fixture.CurrentTime = startTime;
        var snapshotId = NewSnapshotId("tc02");

        await Fixture.Handler.HandleAsync(
            CreateMessage(snapshotId, "instruments", """{"positions":[]}"""),
            CancellationToken.None);

        var advancedTime = startTime.AddMinutes(7);
        Fixture.CurrentTime = advancedTime;
        var calculations = CreateMessage(snapshotId, "calculations", """{"nav":1234.56,"ccy":"USD"}""");
        await Fixture.Handler.HandleAsync(calculations, CancellationToken.None);

        var expectedRootPath =
            $"portfolio_snapshots/year={startTime:yyyy}/month={startTime:MM}/accountId={AccountId}/snapshotId={snapshotId}";
        var blobText = await DownloadBlobTextAsync($"{expectedRootPath}/calculations.json");
        Assert.Equal(calculations.Payload.GetRawText(), blobText);

        var tracking = await GetTrackingAsync(snapshotId);
        Assert.Equal(new[] { "instruments.json", "calculations.json" }, tracking.ReceivedFiles);
        Assert.Equal(advancedTime.UtcDateTime, tracking.LastUpdatedAt);
        Assert.Equal(startTime.UtcDateTime, tracking.FirstReceivedAt);
        Assert.Equal(SnapshotTrackingStatus.Receiving, tracking.Status);
        Assert.Equal(expectedRootPath, tracking.AdlsRootPath);

        Assert.False(await IndexRowExistsAsync(snapshotId));
    }

    [Fact]
    public async Task Out_of_order_payloads_accumulate_correctly_and_complete_only_when_header_arrives_last()
    {
        var startTime = new DateTimeOffset(2026, 7, 13, 11, 0, 0, TimeSpan.Zero);
        Fixture.CurrentTime = startTime;
        var snapshotId = NewSnapshotId("tc03");

        foreach (var (payloadType, payloadJson) in new[]
        {
            ("instruments", """{"positions":[{"isin":"US0378331005","qty":42}]}"""),
            ("settings", """{"tolerance":0.01}"""),
            ("calculations", """{"nav":9876.54,"ccy":"CHF"}"""),
        })
        {
            await Fixture.Handler.HandleAsync(
                CreateMessage(snapshotId, payloadType, payloadJson),
                CancellationToken.None);

            // No premature completion after any non-final payload.
            var partial = await GetTrackingAsync(snapshotId);
            Assert.Equal(SnapshotTrackingStatus.Receiving, partial.Status);
            Assert.False(await IndexRowExistsAsync(snapshotId));
        }

        var headerJson = $$"""
            {
              "eventType": "REBALANCE",
              "portfolioStatus": "APPROVED",
              "orderStatus": "SENT",
              "benchmark": "MSCI World",
              "baseCcy": "CHF",
              "programId": "PRG-7",
              "batchId": "BATCH-2026-07-13",
              "numOrders": 17,
              "ptcAlerts": 2,
              "orderApprovedBy": "approver@ubs.com",
              "orderApprovedAt": "2026-07-13T10:45:00Z",
              "orderSentBy": "sender@ubs.com",
              "orderSentAt": "2026-07-13T10:50:00Z"
            }
            """;
        await Fixture.Handler.HandleAsync(
            CreateMessage(snapshotId, "header", headerJson),
            CancellationToken.None);

        var tracking = await GetTrackingAsync(snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Complete, tracking.Status);
        Assert.NotNull(tracking.CompletedAt);

        var rootPath = tracking.AdlsRootPath;
        foreach (var fileName in new[] { "header.json", "instruments.json", "settings.json", "calculations.json" })
        {
            Assert.True(
                await Fixture.BlobContainer.GetBlobClient($"{rootPath}/{fileName}").ExistsAsync(),
                $"Expected blob {rootPath}/{fileName} to exist.");
        }

        var index = await GetIndexAsync(snapshotId);
        Assert.Equal(snapshotId, index.SnapshotId);
        Assert.Equal(AccountId, index.AccountId);
        Assert.Equal(tracking.FirstReceivedAt, index.SnapshotDate);
        Assert.Equal("REBALANCE", index.EventType);
        Assert.Equal(rootPath, index.AdlsPath);
        Assert.Equal("MSCI World", index.DisplayData.Benchmark);
        Assert.Equal("CHF", index.DisplayData.BaseCcy);
        Assert.Equal("PRG-7", index.DisplayData.ProgramId);
        Assert.Equal("BATCH-2026-07-13", index.DisplayData.BatchId);
        Assert.Equal(17, index.DisplayData.NumOrders);
        Assert.Equal(2, index.DisplayData.PtcAlerts);
        Assert.Equal("approver@ubs.com", index.DisplayData.OrderApprovedBy);
        Assert.Equal(new DateTime(2026, 7, 13, 10, 45, 0, DateTimeKind.Utc), index.DisplayData.OrderApprovedAt);
        Assert.Equal("sender@ubs.com", index.DisplayData.OrderSentBy);
        Assert.Equal(new DateTime(2026, 7, 13, 10, 50, 0, DateTimeKind.Utc), index.DisplayData.OrderSentAt);
    }

    private SnapshotMessage CreateMessage(string snapshotId, string payloadType, string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);

        return new SnapshotMessage
        {
            SnapshotId = snapshotId,
            AccountId = AccountId,
            SnapshotType = "portfolio",
            PayloadType = payloadType,
            Stage = "PreTrade",
            PublishedAt = Fixture.CurrentTime.UtcDateTime,
            PublishedBy = "PortfolioCalculation",
            SchemaVersion = "1.0",
            Payload = document.RootElement.Clone(),
        };
    }

    private async Task<string> DownloadBlobTextAsync(string blobName)
    {
        var download = await Fixture.BlobContainer.GetBlobClient(blobName).DownloadContentAsync();
        return download.Value.Content.ToString();
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
