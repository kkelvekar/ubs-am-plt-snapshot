using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UBS.AM.PLT.SnapshotWriter.Domain;
using UBS.AM.PLT.SnapshotWriter.Domain.Entities;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence;

namespace UBS.AM.PLT.SnapshotWriter.IntegrationTests;

/// <summary>
/// Group F — failure scenarios (TC-16..TC-20), design doc §8. This project (Mode A)
/// only covers the piece of Group F that is deterministic and Kafka-independent:
/// TC-20 Part 1 — redelivery of the completing (header) message for an
/// already-COMPLETE snapshot, simulating what happens when the index write and
/// <c>MarkCompleteAsync</c> both succeed but the Kafka offset commit afterwards fails
/// (design doc §8 Scenario 5) and the consumer therefore redelivers the same message.
/// The rest of Group F (TC-16..TC-19, and TC-20 Part 2's live broker-pause proof)
/// requires the real Kafka consume/commit path and is exercised live in Mode B via
/// <c>tools/fault-injection.ps1</c> — see that script's help text for the executed
/// procedures and evidence.
/// </summary>
public sealed class GroupFFailureScenarioTests : IntegrationTestBase, IClassFixture<SnapshotWriterFixture>
{
    // Fresh account id — IT-ACC-001..IT-ACC-005 are already used by Groups A/B/C.
    private const string AccountId = "IT-ACC-006";

    public GroupFFailureScenarioTests(SnapshotWriterFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task Redelivery_of_completing_message_after_index_commit_is_a_harmless_noop()
    {
        // Arrange: drive a full 4-payload snapshot to COMPLETE, exactly like Group B.
        var startTime = new DateTimeOffset(2026, 7, 14, 9, 0, 0, TimeSpan.Zero);
        Fixture.CurrentTime = startTime;
        var snapshotId = NewSnapshotId("tc20p1");

        var header = CreateMessage(snapshotId, "header", TestPayloads.HeaderJson);

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
        }

        // The header is the 4th and final required file — this first HandleAsync call
        // is the one that would, in the live system, be followed by a failed
        // consumer.Commit() (design doc §8 Scenario 5: index write succeeds, offset
        // commit fails).
        await Fixture.Handler.HandleAsync(header, CancellationToken.None);

        var trackingAfterFirstAttempt = await GetTrackingAsync(snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Complete, trackingAfterFirstAttempt.Status);
        Assert.NotNull(trackingAfterFirstAttempt.CompletedAt);

        var indexAfterFirstAttempt = await GetIndexAsync(snapshotId);
        var headerBlobTextAfterFirstAttempt =
            await DownloadBlobTextAsync($"{trackingAfterFirstAttempt.AdlsRootPath}/header.json");

        // Advance time so a naive "insert" or "touch" implementation would visibly leak
        // through into created_at/completed_at if it re-ran instead of no-op'ing.
        Fixture.CurrentTime = Fixture.CurrentTime.AddMinutes(3);

        // Act: redeliver the SAME completing header message — simulating the consumer
        // re-processing after a failed offset commit for an already-COMPLETE snapshot.
        var exception = await Record.ExceptionAsync(
            () => Fixture.Handler.HandleAsync(header, CancellationToken.None));

        // Assert: no exception propagates from the handler on redelivery.
        Assert.Null(exception);

        // Exactly one tracking row, still COMPLETE, completed_at unchanged (touch-only,
        // not a re-completion).
        await using (var context = await Fixture.DbContextFactory.CreateDbContextAsync())
        {
            var trackingRowCount = await context.SnapshotTracking
                .AsNoTracking()
                .CountAsync(e => e.SnapshotId == snapshotId);
            Assert.Equal(1, trackingRowCount);
        }

        var trackingAfterRedelivery = await GetTrackingAsync(snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Complete, trackingAfterRedelivery.Status);
        Assert.Equal(trackingAfterFirstAttempt.CompletedAt, trackingAfterRedelivery.CompletedAt);
        Assert.Equal(trackingAfterFirstAttempt.FirstReceivedAt, trackingAfterRedelivery.FirstReceivedAt);
        Assert.Equal(trackingAfterFirstAttempt.AdlsRootPath, trackingAfterRedelivery.AdlsRootPath);
        Assert.Equal(trackingAfterFirstAttempt.ReceivedFiles, trackingAfterRedelivery.ReceivedFiles);

        // Exactly one index row, created_at unchanged — an UPSERT touch, not a new row
        // and not a re-created row.
        await using (var context = await Fixture.DbContextFactory.CreateDbContextAsync())
        {
            var indexRowCount = await context.SnapshotIndex
                .AsNoTracking()
                .CountAsync(e => e.SnapshotId == snapshotId);
            Assert.Equal(1, indexRowCount);
        }

        var indexAfterRedelivery = await GetIndexAsync(snapshotId);
        Assert.Equal(indexAfterFirstAttempt.CreatedAt, indexAfterRedelivery.CreatedAt);
        Assert.Equal(indexAfterFirstAttempt.AccountId, indexAfterRedelivery.AccountId);
        Assert.Equal(indexAfterFirstAttempt.AdlsPath, indexAfterRedelivery.AdlsPath);
        Assert.Equal(indexAfterFirstAttempt.SnapshotDate, indexAfterRedelivery.SnapshotDate);
        Assert.Equal(indexAfterFirstAttempt.EventType, indexAfterRedelivery.EventType);
        Assert.Equal(indexAfterFirstAttempt.DisplayData.Benchmark, indexAfterRedelivery.DisplayData.Benchmark);
        Assert.Equal(indexAfterFirstAttempt.DisplayData.BatchId, indexAfterRedelivery.DisplayData.BatchId);

        // Blob overwritten (content-idempotent), not duplicated: same content as before.
        var headerBlobTextAfterRedelivery =
            await DownloadBlobTextAsync($"{trackingAfterRedelivery.AdlsRootPath}/header.json");
        Assert.Equal(headerBlobTextAfterFirstAttempt, headerBlobTextAfterRedelivery);
        Assert.Equal(header.Payload.GetRawText(), headerBlobTextAfterRedelivery);
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
}
