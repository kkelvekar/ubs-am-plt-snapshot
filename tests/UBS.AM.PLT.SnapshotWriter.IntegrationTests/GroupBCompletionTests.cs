using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UBS.AM.PLT.SnapshotWriter.Domain;
using UBS.AM.PLT.SnapshotWriter.Domain.Entities;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence;

namespace UBS.AM.PLT.SnapshotWriter.IntegrationTests;

/// <summary>
/// Group B — completion scenarios (TC-04..TC-06), run against REAL Azure resources
/// (ADLS Gen2 + Azure SQL) with Kafka bypassed, same as Group A: every message goes
/// straight through the real <c>ISnapshotMessageHandler</c> resolved from the production
/// DI graph. TC-04 drills into the completion mechanics (field-by-field index assertions);
/// TC-05/TC-06 prove canonical and shuffled arrival orders reach the structurally
/// identical end state via a shared end-state assertion.
/// </summary>
public sealed class GroupBCompletionTests : IntegrationTestBase, IClassFixture<SnapshotWriterFixture>
{
    private const string AccountId = "IT-ACC-002";

    public GroupBCompletionTests(SnapshotWriterFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task Final_payload_completes_the_set_and_writes_index_via_upsert()
    {
        var startTime = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        Fixture.CurrentTime = startTime;
        var snapshotId = NewSnapshotId("tc04");

        foreach (var (payloadType, payloadJson) in new[]
        {
            ("instruments", """{"positions":[{"isin":"CH0038863350","qty":250}]}"""),
            ("calculations", """{"nav":5555.55,"ccy":"CHF"}"""),
            ("settings", """{"tolerance":0.05}"""),
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

        // The header is the 4th and final required file — its arrival completes the set.
        var header = CreateMessage(snapshotId, "header", TestPayloads.HeaderJson);
        await Fixture.Handler.HandleAsync(header, CancellationToken.None);

        var tracking = await GetTrackingAsync(snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Complete, tracking.Status);
        Assert.NotNull(tracking.CompletedAt);

        // The header blob holds exactly what was sent — proof the payload stayed opaque.
        var headerBlobText = await DownloadBlobTextAsync($"{tracking.AdlsRootPath}/header.json");
        Assert.Equal(header.Payload.GetRawText(), headerBlobText);

        // Index row written via UPSERT, with every HeaderPayload field flowed into display_data.
        var index = await GetIndexAsync(snapshotId);
        Assert.Equal(snapshotId, index.SnapshotId);
        Assert.Equal(AccountId, index.AccountId);
        Assert.Equal(tracking.AdlsRootPath, index.AdlsPath);
        Assert.Equal(TestPayloads.ExpectedHeader.EventType, index.EventType);
        Assert.Equal(TestPayloads.ExpectedHeader.Benchmark, index.DisplayData.Benchmark);
        Assert.Equal(TestPayloads.ExpectedHeader.BaseCcy, index.DisplayData.BaseCcy);
        Assert.Equal(TestPayloads.ExpectedHeader.ProgramId, index.DisplayData.ProgramId);
        Assert.Equal(TestPayloads.ExpectedHeader.BatchId, index.DisplayData.BatchId);
        Assert.Equal(TestPayloads.ExpectedHeader.NumOrders, index.DisplayData.NumOrders);
        Assert.Equal(TestPayloads.ExpectedHeader.PtcAlerts, index.DisplayData.PtcAlerts);
        Assert.Equal(TestPayloads.ExpectedHeader.OrderApprovedBy, index.DisplayData.OrderApprovedBy);
        Assert.Equal(TestPayloads.ExpectedHeader.OrderApprovedAt, index.DisplayData.OrderApprovedAt);
        Assert.Equal(TestPayloads.ExpectedHeader.OrderSentBy, index.DisplayData.OrderSentBy);
        Assert.Equal(TestPayloads.ExpectedHeader.OrderSentAt, index.DisplayData.OrderSentAt);
    }

    [Fact]
    public async Task Full_snapshot_in_canonical_order_is_queryable_with_correct_display_data()
    {
        Fixture.CurrentTime = new DateTimeOffset(2026, 7, 13, 13, 0, 0, TimeSpan.Zero);
        var snapshotId = NewSnapshotId("tc05");

        // Canonical order = the TestProducer's own payload order in
        // tools/.../Data/snapshot-simulation-data.json: header, instruments, calculations, settings.
        foreach (var (payloadType, payloadJson) in new[]
        {
            ("header", TestPayloads.HeaderJson),
            ("instruments", """{"positions":[{"isin":"US5949181045","qty":8}]}"""),
            ("calculations", """{"nav":100200.30,"ccy":"USD"}"""),
            ("settings", """{"tolerance":0.10}"""),
        })
        {
            await Fixture.Handler.HandleAsync(
                CreateMessage(snapshotId, payloadType, payloadJson),
                CancellationToken.None);
        }

        await AssertWellFormedCompletedSnapshotAsync(snapshotId);
    }

    [Fact]
    public async Task Full_snapshot_in_shuffled_order_reaches_the_same_end_state_as_canonical_order()
    {
        Fixture.CurrentTime = new DateTimeOffset(2026, 7, 13, 14, 0, 0, TimeSpan.Zero);
        var snapshotId = NewSnapshotId("tc06");

        // Shuffled order with instruments — not header — arriving LAST: completion must
        // trigger on whichever message completes the set, proving the completeness check
        // is set-membership over required files, not order- or header-position-dependent.
        foreach (var (payloadType, payloadJson) in new[]
        {
            ("settings", """{"tolerance":0.10}"""),
            ("calculations", """{"nav":100200.30,"ccy":"USD"}"""),
            ("header", TestPayloads.HeaderJson),
            ("instruments", """{"positions":[{"isin":"US5949181045","qty":8}]}"""),
        })
        {
            await Fixture.Handler.HandleAsync(
                CreateMessage(snapshotId, payloadType, payloadJson),
                CancellationToken.None);
        }

        // Same end-state assertion as TC-05: arrival order must not change the outcome.
        await AssertWellFormedCompletedSnapshotAsync(snapshotId);
    }

    /// <summary>
    /// Shared end-state contract for a fully received snapshot, used by both the canonical
    /// (TC-05) and shuffled (TC-06) order tests so their structural equivalence is explicit:
    /// tracking COMPLETE, all four blobs under one root, and an index row whose display_data
    /// matches the header payload that was sent.
    /// </summary>
    private async Task AssertWellFormedCompletedSnapshotAsync(string snapshotId)
    {
        var tracking = await GetTrackingAsync(snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Complete, tracking.Status);
        Assert.NotNull(tracking.CompletedAt);

        foreach (var fileName in new[] { "header.json", "instruments.json", "calculations.json", "settings.json" })
        {
            Assert.True(
                await Fixture.BlobContainer.GetBlobClient($"{tracking.AdlsRootPath}/{fileName}").ExistsAsync(),
                $"Expected blob {tracking.AdlsRootPath}/{fileName} to exist.");
        }

        var index = await GetIndexAsync(snapshotId);
        Assert.Equal(snapshotId, index.SnapshotId);
        Assert.Equal(AccountId, index.AccountId);
        Assert.Equal(tracking.AdlsRootPath, index.AdlsPath);
        Assert.Equal(tracking.FirstReceivedAt, index.SnapshotDate);
        Assert.Equal(TestPayloads.ExpectedHeader.EventType, index.EventType);
        Assert.Equal(TestPayloads.ExpectedHeader.Benchmark, index.DisplayData.Benchmark);
        Assert.Equal(TestPayloads.ExpectedHeader.BaseCcy, index.DisplayData.BaseCcy);
        Assert.Equal(TestPayloads.ExpectedHeader.ProgramId, index.DisplayData.ProgramId);
        Assert.Equal(TestPayloads.ExpectedHeader.BatchId, index.DisplayData.BatchId);
        Assert.Equal(TestPayloads.ExpectedHeader.NumOrders, index.DisplayData.NumOrders);
        Assert.Equal(TestPayloads.ExpectedHeader.PtcAlerts, index.DisplayData.PtcAlerts);
        Assert.Equal(TestPayloads.ExpectedHeader.OrderApprovedBy, index.DisplayData.OrderApprovedBy);
        Assert.Equal(TestPayloads.ExpectedHeader.OrderApprovedAt, index.DisplayData.OrderApprovedAt);
        Assert.Equal(TestPayloads.ExpectedHeader.OrderSentBy, index.DisplayData.OrderSentBy);
        Assert.Equal(TestPayloads.ExpectedHeader.OrderSentAt, index.DisplayData.OrderSentAt);
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
