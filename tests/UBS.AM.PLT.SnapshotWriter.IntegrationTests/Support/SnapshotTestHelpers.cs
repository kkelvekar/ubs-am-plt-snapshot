using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UBS.AM.PLT.SnapshotWriter.Domain;
using UBS.AM.PLT.SnapshotWriter.Domain.Entities;

namespace UBS.AM.PLT.SnapshotWriter.IntegrationTests.Support;

/// <summary>
/// Message-construction and result-reading helpers shared by the Gherkin step definitions,
/// lifted verbatim from the old xUnit group tests so every converted group re-uses one copy
/// rather than duplicating them per step-definition class. All read helpers hit the same real
/// Azure resources (ADLS Gen2 blobs + Azure SQL) as the production handler under test.
/// </summary>
internal static class SnapshotTestHelpers
{
    public static SnapshotMessage CreateMessage(
        SnapshotWriterFixture fixture,
        string snapshotId,
        string accountId,
        string payloadType,
        string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);

        return new SnapshotMessage
        {
            SnapshotId = snapshotId,
            AccountId = accountId,
            SnapshotType = "portfolio",
            PayloadType = payloadType,
            Stage = "PreTrade",
            PublishedAt = fixture.CurrentTime.UtcDateTime,
            PublishedBy = "PortfolioCalculation",
            SchemaVersion = "1.0",
            Payload = document.RootElement.Clone(),
        };
    }

    public static async Task<string> DownloadBlobTextAsync(SnapshotWriterFixture fixture, string blobName)
    {
        var download = await fixture.BlobContainer.GetBlobClient(blobName).DownloadContentAsync();
        return download.Value.Content.ToString();
    }

    public static async Task<SnapshotTrackingEntity> GetTrackingAsync(SnapshotWriterFixture fixture, string snapshotId)
    {
        await using var context = await fixture.DbContextFactory.CreateDbContextAsync();
        return await context.SnapshotTracking
            .AsNoTracking()
            .SingleAsync(e => e.SnapshotId == snapshotId);
    }

    public static async Task<SnapshotIndexEntity> GetIndexAsync(SnapshotWriterFixture fixture, string snapshotId)
    {
        await using var context = await fixture.DbContextFactory.CreateDbContextAsync();
        return await context.SnapshotIndex
            .AsNoTracking()
            .SingleAsync(e => e.SnapshotId == snapshotId);
    }

    public static async Task<bool> IndexRowExistsAsync(SnapshotWriterFixture fixture, string snapshotId)
    {
        await using var context = await fixture.DbContextFactory.CreateDbContextAsync();
        return await context.SnapshotIndex
            .AsNoTracking()
            .AnyAsync(e => e.SnapshotId == snapshotId);
    }
}
