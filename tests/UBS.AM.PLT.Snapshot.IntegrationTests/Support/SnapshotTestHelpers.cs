using System.Globalization;
using Microsoft.EntityFrameworkCore;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;

namespace UBS.AM.PLT.Snapshot.IntegrationTests.Support;

/// <summary>
/// Message-construction and result-reading helpers shared by the Gherkin step definitions,
/// lifted verbatim from the old xUnit group tests so every converted group re-uses one copy
/// rather than duplicating them per step-definition class. All read helpers hit the same real
/// Azure resources (ADLS Gen2 blobs + Azure SQL) as the production handler under test.
/// </summary>
internal static class SnapshotTestHelpers
{
    public static SnapshotMessage CreateMessage(
        SnapshotFixture fixture,
        string snapshotId,
        string accountId,
        string payloadType,
        string payloadJson)
        => new()
        {
            SnapshotId = snapshotId,
            AccountId = accountId,
            SnapshotType = "portfolio",
            PayloadType = payloadType,
            PublishedAt = fixture.CurrentTime.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            PublishedBy = "PortfolioCalculation",
            Payload = payloadJson,
        };

    public static async Task<string> DownloadBlobTextAsync(SnapshotFixture fixture, string blobName)
    {
        var download = await fixture.BlobContainer.GetBlobClient(blobName).DownloadContentAsync();
        return download.Value.Content.ToString();
    }

    public static async Task<SnapshotTrackingEntity> GetTrackingAsync(SnapshotFixture fixture, string snapshotId)
    {
        await using var context = await fixture.DbContextFactory.CreateDbContextAsync();
        return await context.SnapshotTracking
            .AsNoTracking()
            .SingleAsync(e => e.SnapshotId == snapshotId);
    }

    public static async Task<SnapshotIndexEntity> GetIndexAsync(SnapshotFixture fixture, string snapshotId)
    {
        await using var context = await fixture.DbContextFactory.CreateDbContextAsync();
        return await context.SnapshotIndex
            .AsNoTracking()
            .SingleAsync(e => e.SnapshotId == snapshotId);
    }

    public static async Task<bool> IndexRowExistsAsync(SnapshotFixture fixture, string snapshotId)
    {
        await using var context = await fixture.DbContextFactory.CreateDbContextAsync();
        return await context.SnapshotIndex
            .AsNoTracking()
            .AnyAsync(e => e.SnapshotId == snapshotId);
    }

    public static async Task<bool> TrackingRowExistsAsync(SnapshotFixture fixture, string snapshotId)
    {
        await using var context = await fixture.DbContextFactory.CreateDbContextAsync();
        return await context.SnapshotTracking
            .AsNoTracking()
            .AnyAsync(e => e.SnapshotId == snapshotId);
    }
}
