using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UBS.AM.PLT.SnapshotWriter.Domain;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence;

namespace UBS.AM.PLT.SnapshotWriter.IntegrationTests;

/// <summary>
/// Group G — redelivery / idempotency (TC-21, TC-22), run against REAL Azure resources
/// (ADLS Gen2 + Azure SQL) with Kafka bypassed: every message goes straight through the
/// real <c>ISnapshotMessageHandler</c> resolved from the production DI graph.
/// Group F's TC-20 already proves redelivery of the completing <c>header</c> message is a
/// no-op; here TC-21 proves the same for a NON-header payload redelivered after the
/// snapshot is already COMPLETE, and TC-22 proves a same-payloadType duplicate arriving
/// BEFORE completion is de-duplicated (counted once) and does not block eventual
/// completion. Mode A can assert the handler stays clean (no exception, no duplicate row);
/// full offset-commit proof under redelivery is Mode B only (tools/fault-injection.ps1).
/// </summary>
public sealed class GroupGRedeliveryIdempotencyTests : IntegrationTestBase, IClassFixture<SnapshotWriterFixture>
{
    // Fresh account id — IT-ACC-001..006 are used by Groups A/B/C/F.
    private const string AccountId = "IT-ACC-007";

    private const string InstrumentsJson = """{"positions":[{"isin":"CH0038863350","qty":250}]}""";
    private const string CalculationsJson = """{"nav":5555.55,"ccy":"CHF"}""";
    private const string SettingsJson = """{"tolerance":0.05}""";

    public GroupGRedeliveryIdempotencyTests(SnapshotWriterFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task Redelivery_of_a_nonheader_payload_after_completion_is_a_harmless_noop()
    {
        // TC-21: drive a full 4-payload snapshot to COMPLETE, then redeliver the exact
        // duplicate of a NON-header payload (instruments.json).
        Fixture.CurrentTime = new DateTimeOffset(2026, 7, 14, 15, 0, 0, TimeSpan.Zero);
        var snapshotId = NewSnapshotId("tc21");

        var instruments = CreateMessage(snapshotId, "instruments", InstrumentsJson);

        await Fixture.Handler.HandleAsync(instruments, CancellationToken.None);
        await Fixture.Handler.HandleAsync(CreateMessage(snapshotId, "calculations", CalculationsJson), CancellationToken.None);
        await Fixture.Handler.HandleAsync(CreateMessage(snapshotId, "settings", SettingsJson), CancellationToken.None);
        await Fixture.Handler.HandleAsync(CreateMessage(snapshotId, "header", TestPayloads.HeaderJson), CancellationToken.None);

        var trackingBefore = await GetTrackingAsync(snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Complete, trackingBefore.Status);
        Assert.NotNull(trackingBefore.CompletedAt);

        var indexBefore = await GetIndexAsync(snapshotId);
        var instrumentsBlobBefore =
            await DownloadBlobTextAsync($"{trackingBefore.AdlsRootPath}/instruments.json");

        // Advance time so a spurious re-completion or re-insert would leak into a timestamp.
        Fixture.CurrentTime = Fixture.CurrentTime.AddMinutes(4);

        // Act: redeliver the SAME instruments message onto the already-COMPLETE snapshot.
        var exception = await Record.ExceptionAsync(
            () => Fixture.Handler.HandleAsync(instruments, CancellationToken.None));

        Assert.Null(exception);

        // Exactly one tracking row, everything unchanged EXCEPT last_updated_at (the update
        // path touches only last_updated_at; status/completed_at/first_received_at/
        // adls_root_path/received_files are all left intact).
        await using (var context = await Fixture.DbContextFactory.CreateDbContextAsync())
        {
            Assert.Equal(1, await context.SnapshotTracking.AsNoTracking().CountAsync(e => e.SnapshotId == snapshotId));
        }

        var trackingAfter = await GetTrackingAsync(snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Complete, trackingAfter.Status);
        Assert.Equal(trackingBefore.CompletedAt, trackingAfter.CompletedAt);
        Assert.Equal(trackingBefore.FirstReceivedAt, trackingAfter.FirstReceivedAt);
        Assert.Equal(trackingBefore.AdlsRootPath, trackingAfter.AdlsRootPath);
        Assert.Equal(trackingBefore.ReceivedFiles, trackingAfter.ReceivedFiles);
        Assert.True(trackingAfter.LastUpdatedAt > trackingBefore.LastUpdatedAt);

        // Exactly one index row, created_at and all fields unchanged — the UPSERT was never
        // re-run (completeness block is skipped once status is no longer RECEIVING).
        await using (var context = await Fixture.DbContextFactory.CreateDbContextAsync())
        {
            Assert.Equal(1, await context.SnapshotIndex.AsNoTracking().CountAsync(e => e.SnapshotId == snapshotId));
        }

        var indexAfter = await GetIndexAsync(snapshotId);
        Assert.Equal(indexBefore.CreatedAt, indexAfter.CreatedAt);
        Assert.Equal(indexBefore.AdlsPath, indexAfter.AdlsPath);
        Assert.Equal(indexBefore.EventType, indexAfter.EventType);
        Assert.Equal(indexBefore.SnapshotDate, indexAfter.SnapshotDate);

        // Blob overwritten with byte-identical content, not duplicated.
        var instrumentsBlobAfter =
            await DownloadBlobTextAsync($"{trackingAfter.AdlsRootPath}/instruments.json");
        Assert.Equal(instrumentsBlobBefore, instrumentsBlobAfter);
        Assert.Equal(instruments.Payload.GetRawText(), instrumentsBlobAfter);
    }

    [Fact]
    public async Task Duplicate_same_payload_before_completion_is_counted_once_and_still_completes()
    {
        // TC-22: the same payloadType (instruments.json) is delivered twice for the same
        // snapshotId BEFORE the set is complete. It must appear once in received_files, the
        // snapshot must stay RECEIVING, and the remaining required files must still drive it
        // to COMPLETE.
        Fixture.CurrentTime = new DateTimeOffset(2026, 7, 14, 16, 0, 0, TimeSpan.Zero);
        var snapshotId = NewSnapshotId("tc22");

        await Fixture.Handler.HandleAsync(
            CreateMessage(snapshotId, "instruments", InstrumentsJson), CancellationToken.None);

        Fixture.CurrentTime = Fixture.CurrentTime.AddMinutes(2);
        await Fixture.Handler.HandleAsync(
            CreateMessage(snapshotId, "instruments", InstrumentsJson), CancellationToken.None);

        var afterDuplicate = await GetTrackingAsync(snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Receiving, afterDuplicate.Status);
        Assert.Equal(new[] { "instruments.json" }, afterDuplicate.ReceivedFiles);
        Assert.False(await IndexRowExistsAsync(snapshotId));

        // The remaining three required files still complete the set.
        await Fixture.Handler.HandleAsync(CreateMessage(snapshotId, "calculations", CalculationsJson), CancellationToken.None);
        await Fixture.Handler.HandleAsync(CreateMessage(snapshotId, "settings", SettingsJson), CancellationToken.None);
        await Fixture.Handler.HandleAsync(CreateMessage(snapshotId, "header", TestPayloads.HeaderJson), CancellationToken.None);

        var tracking = await GetTrackingAsync(snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Complete, tracking.Status);
        Assert.NotNull(tracking.CompletedAt);
        // instruments.json still appears exactly once despite the duplicate delivery.
        Assert.Single(tracking.ReceivedFiles, f => f == "instruments.json");
        Assert.Equal(4, tracking.ReceivedFiles.Count);

        Assert.True(await IndexRowExistsAsync(snapshotId));
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
