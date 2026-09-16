using Microsoft.EntityFrameworkCore;
using UBS.AM.PLT.Snapshot.Application.Features.LiveTestCleanup;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Infrastructure.Adls;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql;

namespace UBS.AM.PLT.Snapshot.IntegrationTests;

public sealed class LiveTestSnapshotCleanupTests(SnapshotFixture fixture) : IClassFixture<SnapshotFixture>
{
    [Fact]
    public async Task Cleanup_deletes_only_marked_snapshot_files_and_both_sql_rows_and_can_be_repeated()
    {
        var testId = LiveTestSnapshot.NewId();
        var ordinaryId = $"it-cleanup-keep-{Guid.NewGuid():N}";
        var sql = new SqlLiveTestSnapshotCleanupStore(fixture.DbContextFactory);
        var blobs = new AzureBlobLiveTestSnapshotCleanup(fixture.BlobContainer);
        LiveTestSnapshotLocation? location = null;
        try
        {
            await SeedAsync(testId);
            await SeedAsync(ordinaryId);
            location = Assert.Single(await sql.ListAsync(default), item => item.SnapshotId == testId);
            Assert.Contains(location, await blobs.ListAsync(default));

            await Assert.ThrowsAsync<InvalidOperationException>(() => sql.DeleteAsync(ordinaryId, default));
            Assert.Equal(6, await blobs.DeleteAsync(location, default));
            Assert.Equal(2, await sql.DeleteAsync(testId, default));
            Assert.Equal(0, await blobs.DeleteAsync(location, default));
            Assert.Equal(0, await sql.DeleteAsync(testId, default));

            await using var context = await fixture.DbContextFactory.CreateDbContextAsync();
            Assert.False(await context.SnapshotTracking.AnyAsync(row => row.SnapshotId == testId));
            Assert.False(await context.PortfolioSnapshotIndex.AnyAsync(row => row.SnapshotId == testId));
            var ordinary = await context.PortfolioSnapshotIndex.SingleAsync(row => row.SnapshotId == ordinaryId);
            Assert.True(await context.SnapshotTracking.AnyAsync(row => row.SnapshotId == ordinaryId));
            Assert.True((await fixture.BlobContainer.GetBlobClient($"{ordinary.AdlsPath}/header.json").ExistsAsync()).Value);
            await foreach (var blob in fixture.BlobContainer.GetBlobsAsync(prefix: location.RootPath + "/"))
            {
                Assert.Fail($"Test blob still exists: {blob.Name}");
            }
        }
        finally
        {
            location ??= (await sql.ListAsync(default)).FirstOrDefault(item => item.SnapshotId == testId);
            if (location is not null)
            {
                await blobs.DeleteAsync(location, default);
            }
            await sql.DeleteAsync(testId, default);
            await fixture.Cleanup.CleanupSnapshotAsync(ordinaryId);
        }
    }

    private async Task SeedAsync(string snapshotId)
    {
        foreach (var (payloadType, payload) in new[]
        {
            ("header", TestPayloads.HeaderJson), ("portfolio", TestPayloads.PortfolioJson),
            ("orders", TestPayloads.OrdersJson), ("compliances", TestPayloads.CompliancesJson),
            ("orders-history", TestPayloads.OrdersHistoryJson), ("settings", TestPayloads.SettingsJson),
        })
        {
            await fixture.Handler.HandleAsync(new SnapshotMessage
            {
                SnapshotId = snapshotId, AccountId = "IT-CLEANUP", SnapshotType = "portfolio",
                PayloadType = payloadType, Payload = payload, PublishedAt = "2026-09-16T00:00:00Z",
                PublishedBy = "IntegrationTests",
            }, default);
        }
    }
}
