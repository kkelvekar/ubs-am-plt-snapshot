using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UBS.AM.PLT.SnapshotWriter.Domain;
using UBS.AM.PLT.SnapshotWriter.Domain.Entities;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence;

namespace UBS.AM.PLT.SnapshotWriter.IntegrationTests;

/// <summary>
/// Group C — concurrency/partitioning scenarios (TC-07, TC-08), run against REAL Azure
/// resources (ADLS Gen2 + Azure SQL) with Kafka bypassed, same as Groups A/B: every
/// message goes straight through the real <c>ISnapshotMessageHandler</c>. Partition
/// assignment itself is invisible to this handler — the invariant under test is that the
/// SQL-keyed write path (tracking/index keyed by snapshotId) never lets one snapshot's
/// processing observe or mutate another's, regardless of how their messages interleave
/// on a single consumer thread. TC-07 strictly alternates two snapshots for two different
/// accounts; TC-08 runs two snapshots sequentially for the same account.
/// </summary>
public sealed class GroupCConcurrencyTests : IntegrationTestBase, IClassFixture<SnapshotWriterFixture>
{
    private const string Tc07AccountA = "IT-ACC-003";
    private const string Tc07AccountB = "IT-ACC-004";
    private const string Tc08Account = "IT-ACC-005";

    public GroupCConcurrencyTests(SnapshotWriterFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task Interleaved_payloads_for_two_snapshots_never_cross_contaminate_tracking_or_index()
    {
        Fixture.CurrentTime = new DateTimeOffset(2026, 7, 13, 15, 0, 0, TimeSpan.Zero);
        var snapshotA = NewSnapshotId("tc07-a");
        var snapshotB = NewSnapshotId("tc07-b");

        var aInstruments = CreateMessage(snapshotA, Tc07AccountA, "instruments", """{"positions":[{"isin":"CH0038863350","qty":300}]}""");
        var bInstruments = CreateMessage(snapshotB, Tc07AccountB, "instruments", """{"positions":[{"isin":"US5949181045","qty":400}]}""");
        var aSettings = CreateMessage(snapshotA, Tc07AccountA, "settings", """{"tolerance":0.03}""");
        var bCalculations = CreateMessage(snapshotB, Tc07AccountB, "calculations", """{"nav":7000.00,"ccy":"USD"}""");
        var aCalculations = CreateMessage(snapshotA, Tc07AccountA, "calculations", """{"nav":8000.00,"ccy":"CHF"}""");
        var bSettings = CreateMessage(snapshotB, Tc07AccountB, "settings", """{"tolerance":0.04}""");
        var aHeader = CreateMessage(snapshotA, Tc07AccountA, "header", TestPayloads.HeaderJson);
        var bHeader = CreateMessage(snapshotB, Tc07AccountB, "header", TestPayloads.HeaderJson);

        // Steps 1-2: strictly alternating first payload for each snapshot.
        await Fixture.Handler.HandleAsync(aInstruments, CancellationToken.None);
        await Fixture.Handler.HandleAsync(bInstruments, CancellationToken.None);

        // Checkpoint 1: each snapshot's tracking row contains only its own single file,
        // both RECEIVING, roots differ by accountId segment, neither has an index row.
        var trackingAAfterCheckpoint1 = await GetTrackingAsync(snapshotA);
        var trackingBAfterCheckpoint1 = await GetTrackingAsync(snapshotB);
        Assert.Equal(new[] { "instruments.json" }, trackingAAfterCheckpoint1.ReceivedFiles);
        Assert.Equal(new[] { "instruments.json" }, trackingBAfterCheckpoint1.ReceivedFiles);
        Assert.Equal(SnapshotTrackingStatus.Receiving, trackingAAfterCheckpoint1.Status);
        Assert.Equal(SnapshotTrackingStatus.Receiving, trackingBAfterCheckpoint1.Status);
        Assert.NotEqual(trackingAAfterCheckpoint1.AdlsRootPath, trackingBAfterCheckpoint1.AdlsRootPath);
        Assert.Contains($"accountId={Tc07AccountA}/", trackingAAfterCheckpoint1.AdlsRootPath);
        Assert.Contains($"accountId={Tc07AccountB}/", trackingBAfterCheckpoint1.AdlsRootPath);
        Assert.False(await IndexRowExistsAsync(snapshotA));
        Assert.False(await IndexRowExistsAsync(snapshotB));

        // Steps 3-6: interleaved middle payloads, alternating snapshots.
        await Fixture.Handler.HandleAsync(aSettings, CancellationToken.None);
        await Fixture.Handler.HandleAsync(bCalculations, CancellationToken.None);
        await Fixture.Handler.HandleAsync(aCalculations, CancellationToken.None);
        await Fixture.Handler.HandleAsync(bSettings, CancellationToken.None);

        // Step 7: A's header is its 4th and final required file — A completes.
        await Fixture.Handler.HandleAsync(aHeader, CancellationToken.None);

        // Checkpoint 2 (cross-contamination kill shot): A is COMPLETE with an index row;
        // B is still RECEIVING with exactly its own three files — no header.json leaked
        // from A's completion — and still no index row for B.
        var trackingAAfterCheckpoint2 = await GetTrackingAsync(snapshotA);
        var trackingBAfterCheckpoint2 = await GetTrackingAsync(snapshotB);
        Assert.Equal(SnapshotTrackingStatus.Complete, trackingAAfterCheckpoint2.Status);
        Assert.NotNull(trackingAAfterCheckpoint2.CompletedAt);
        Assert.True(await IndexRowExistsAsync(snapshotA));

        Assert.Equal(SnapshotTrackingStatus.Receiving, trackingBAfterCheckpoint2.Status);
        Assert.Equal(
            new[] { "instruments.json", "calculations.json", "settings.json" },
            trackingBAfterCheckpoint2.ReceivedFiles);
        Assert.DoesNotContain("header.json", trackingBAfterCheckpoint2.ReceivedFiles);
        Assert.Null(trackingBAfterCheckpoint2.CompletedAt);
        Assert.False(await IndexRowExistsAsync(snapshotB));

        // Step 8: B's header arrives last — B completes too.
        await Fixture.Handler.HandleAsync(bHeader, CancellationToken.None);

        // Final: both COMPLETE, both index rows present with correct account ids, each
        // blob living only under its own root and holding exactly what was sent for it.
        var finalTrackingA = await GetTrackingAsync(snapshotA);
        var finalTrackingB = await GetTrackingAsync(snapshotB);
        Assert.Equal(SnapshotTrackingStatus.Complete, finalTrackingA.Status);
        Assert.Equal(SnapshotTrackingStatus.Complete, finalTrackingB.Status);

        var indexA = await GetIndexAsync(snapshotA);
        var indexB = await GetIndexAsync(snapshotB);
        Assert.Equal(Tc07AccountA, indexA.AccountId);
        Assert.Equal(Tc07AccountB, indexB.AccountId);
        Assert.Equal(finalTrackingA.AdlsRootPath, indexA.AdlsPath);
        Assert.Equal(finalTrackingB.AdlsRootPath, indexB.AdlsPath);

        var aInstrumentsBlobText = await DownloadBlobTextAsync($"{finalTrackingA.AdlsRootPath}/instruments.json");
        var bInstrumentsBlobText = await DownloadBlobTextAsync($"{finalTrackingB.AdlsRootPath}/instruments.json");
        Assert.Equal(aInstruments.Payload.GetRawText(), aInstrumentsBlobText);
        Assert.Equal(bInstruments.Payload.GetRawText(), bInstrumentsBlobText);
        Assert.NotEqual(aInstrumentsBlobText, bInstrumentsBlobText);
    }

    [Fact]
    public async Task Two_sequential_snapshots_for_the_same_account_stay_isolated_by_snapshotId()
    {
        Fixture.CurrentTime = new DateTimeOffset(2026, 7, 13, 16, 0, 0, TimeSpan.Zero);
        var snapshot1 = NewSnapshotId("tc08-1");

        foreach (var (payloadType, payloadJson) in new[]
        {
            ("header", TestPayloads.HeaderJson),
            ("instruments", """{"positions":[{"isin":"CH0012032048","qty":11}]}"""),
            ("calculations", """{"nav":1111.11,"ccy":"CHF"}"""),
            ("settings", """{"tolerance":0.11}"""),
        })
        {
            await Fixture.Handler.HandleAsync(
                CreateMessage(snapshot1, Tc08Account, payloadType, payloadJson),
                CancellationToken.None);
        }

        var trackingS1AfterCompletion = await GetTrackingAsync(snapshot1);
        Assert.Equal(SnapshotTrackingStatus.Complete, trackingS1AfterCompletion.Status);
        var indexS1AfterCompletion = await GetIndexAsync(snapshot1);

        // Advance time before starting the second snapshot for the same account.
        Fixture.CurrentTime = Fixture.CurrentTime.AddMinutes(20);
        var snapshot2 = NewSnapshotId("tc08-2");

        foreach (var (payloadType, payloadJson) in new[]
        {
            ("header", TestPayloads.HeaderJson),
            ("instruments", """{"positions":[{"isin":"US0378331005","qty":22}]}"""),
            ("calculations", """{"nav":2222.22,"ccy":"USD"}"""),
            ("settings", """{"tolerance":0.22}"""),
        })
        {
            await Fixture.Handler.HandleAsync(
                CreateMessage(snapshot2, Tc08Account, payloadType, payloadJson),
                CancellationToken.None);
        }

        var trackingS2 = await GetTrackingAsync(snapshot2);
        Assert.Equal(SnapshotTrackingStatus.Complete, trackingS2.Status);
        var indexS2 = await GetIndexAsync(snapshot2);

        // S1's tracking row must be byte-for-byte unchanged by S2's processing.
        var trackingS1AfterS2 = await GetTrackingAsync(snapshot1);
        Assert.Equal(trackingS1AfterCompletion.ReceivedFiles, trackingS1AfterS2.ReceivedFiles);
        Assert.Equal(trackingS1AfterCompletion.Status, trackingS1AfterS2.Status);
        Assert.Equal(trackingS1AfterCompletion.CompletedAt, trackingS1AfterS2.CompletedAt);
        Assert.Equal(trackingS1AfterCompletion.LastUpdatedAt, trackingS1AfterS2.LastUpdatedAt);
        Assert.Equal(trackingS1AfterCompletion.FirstReceivedAt, trackingS1AfterS2.FirstReceivedAt);
        Assert.Equal(trackingS1AfterCompletion.AdlsRootPath, trackingS1AfterS2.AdlsRootPath);

        // S1's index row must also be byte-for-byte unchanged.
        var indexS1AfterS2 = await GetIndexAsync(snapshot1);
        Assert.Equal(indexS1AfterCompletion.SnapshotId, indexS1AfterS2.SnapshotId);
        Assert.Equal(indexS1AfterCompletion.AccountId, indexS1AfterS2.AccountId);
        Assert.Equal(indexS1AfterCompletion.SnapshotDate, indexS1AfterS2.SnapshotDate);
        Assert.Equal(indexS1AfterCompletion.AdlsPath, indexS1AfterS2.AdlsPath);
        Assert.Equal(indexS1AfterCompletion.CreatedAt, indexS1AfterS2.CreatedAt);

        // Both snapshots share the account root but have distinct snapshotId= subfolders,
        // and two distinct index rows exist.
        Assert.NotEqual(trackingS1AfterS2.AdlsRootPath, trackingS2.AdlsRootPath);
        var expectedSharedPrefix = trackingS1AfterS2.AdlsRootPath[..trackingS1AfterS2.AdlsRootPath.LastIndexOf('/')];
        Assert.StartsWith(expectedSharedPrefix, trackingS2.AdlsRootPath, StringComparison.Ordinal);
        Assert.Contains($"snapshotId={snapshot1}", trackingS1AfterS2.AdlsRootPath);
        Assert.Contains($"snapshotId={snapshot2}", trackingS2.AdlsRootPath);
        Assert.NotEqual(indexS1AfterS2.SnapshotId, indexS2.SnapshotId);
    }

    private SnapshotMessage CreateMessage(string snapshotId, string accountId, string payloadType, string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);

        return new SnapshotMessage
        {
            SnapshotId = snapshotId,
            AccountId = accountId,
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
