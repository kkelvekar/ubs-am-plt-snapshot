using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UBS.AM.PLT.SnapshotWriter.Application;
using UBS.AM.PLT.SnapshotWriter.Domain;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Blob;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Configuration;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence;
using Xunit;

namespace UBS.AM.PLT.SnapshotWriter.IntegrationTests;

/// <summary>
/// Mode A (in-process) integration tests for slice 4 (completeness check + index
/// UPSERT — write-order steps 3-4), per AGENTS.md and the brief's acceptance criteria.
/// Builds <see cref="SnapshotMessage"/> envelopes in code and feeds them directly into
/// <see cref="SnapshotMessageHandler"/> wired to the real <see cref="AzureBlobSnapshotStore"/>
/// (Azurite), the real <see cref="SqlSnapshotTrackingStore"/> / <see cref="SqlSnapshotIndexStore"/>
/// (local SQL Server) and the real <see cref="SnapshotConfigRequiredFilesProvider"/>
/// (bound from this project's appsettings.json <c>SnapshotConfig</c> section) — Kafka is
/// never involved. Requires Azurite (<c>pwsh ./tools/azurite-local.ps1 -Up</c>) and SQL
/// Server with both <c>db/scripts/001_snapshot_tracking.sql</c> and
/// <c>db/scripts/002_snapshot_index.sql</c> applied; connection details come from
/// appsettings.json / environment variables (see <see cref="TestConfiguration"/>), never
/// hardcoded.
///
/// Each test uses a unique snapshotId (a fresh GUID) so tests never collide with each
/// other or with data left behind by other tooling (e.g. the Mode B live worker run), and
/// each test deletes the blobs, tracking rows and index rows it wrote in
/// <see cref="DisposeAsync"/> so the suite leaves no residue in the shared
/// Azurite/SQL Server containers.
/// </summary>
public sealed class SnapshotCompletionIntegrationTests : IAsyncLifetime
{
    private const string HeaderJson = """
        {
          "eventType":       "ModelChange",
          "portfolioStatus": "ReadyToSend",
          "orderStatus":     "ReadyToSend",
          "benchmark":       "MCCHM2EQ",
          "baseCcy":         "CHF",
          "orderApprovedBy": "Anna Miller",
          "orderApprovedAt": "2026-05-15T06:10:14Z",
          "orderSentBy":     "James Smith",
          "orderSentAt":     "2026-05-15T06:14:22Z",
          "programId":       "123456",
          "batchId":         "15884",
          "numOrders":       4,
          "ptcAlerts":       0
        }
        """;

    /// <summary>
    /// Canonical first-arrival time most tests start their <see cref="SteppingTimeProvider"/>
    /// at — the handler pins each snapshot's root folder at this instant, making blob
    /// paths deterministic for assertions and cleanup.
    /// </summary>
    private static readonly DateTimeOffset StartTime = new(2026, 5, 22, 6, 10, 14, TimeSpan.Zero);

    private readonly BlobContainerClient _container;
    private readonly BlobStorageOptions _blobOptions;
    private readonly DbContextOptions<SnapshotWriterDbContext> _dbOptions;
    private readonly List<string> _blobNamesToCleanUp = [];
    private readonly List<string> _snapshotIdsToCleanUp = [];

    public SnapshotCompletionIntegrationTests()
    {
        _blobOptions = TestConfiguration.BlobStorageOptions;
        _container = new BlobContainerClient(_blobOptions.ConnectionString, _blobOptions.ContainerName);

        _dbOptions = new DbContextOptionsBuilder<SnapshotWriterDbContext>()
            .UseSqlServer(TestConfiguration.DatabaseOptions.ConnectionString)
            .Options;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var blobName in _blobNamesToCleanUp)
        {
            await _container.DeleteBlobIfExistsAsync(blobName);
        }

        if (_snapshotIdsToCleanUp.Count > 0)
        {
            await using var context = new SnapshotWriterDbContext(_dbOptions);
            await context.SnapshotTracking
                .Where(e => _snapshotIdsToCleanUp.Contains(e.SnapshotId))
                .ExecuteDeleteAsync();
            await context.SnapshotIndex
                .Where(e => _snapshotIdsToCleanUp.Contains(e.SnapshotId))
                .ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Three_of_four_required_payloads_leaves_tracking_receiving_with_no_index_row()
    {
        var snapshotId = NewSnapshotId();
        var timeProvider = new SteppingTimeProvider(StartTime);
        var handler = CreateHandler(timeProvider);

        var instruments = CreateMessage(snapshotId, "instruments", """{"total":21}""");
        var calculations = CreateMessage(snapshotId, "calculations", """{"pnl":100}""");
        var settings = CreateMessage(snapshotId, "settings", """{"rebalance":true}""");
        Track(instruments, calculations, settings);

        await handler.HandleAsync(instruments, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await handler.HandleAsync(calculations, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await handler.HandleAsync(settings, CancellationToken.None);

        var tracking = await LoadTrackingEntryAsync(snapshotId);

        Assert.NotNull(tracking);
        Assert.Equal(SnapshotTrackingStatus.Receiving, tracking!.Status);
        Assert.Null(tracking.CompletedAt);
        Assert.Equal(3, tracking.ReceivedFiles.Count);
        Assert.Null(await LoadIndexEntryAsync(snapshotId));
    }

    [Fact]
    public async Task Fourth_required_payload_completes_the_snapshot_and_writes_the_index_row()
    {
        var snapshotId = NewSnapshotId();
        var timeProvider = new SteppingTimeProvider(StartTime);
        var handler = CreateHandler(timeProvider);

        // header is deliberately the LAST (completing) payload to arrive here — the
        // complementary "header arrives first" ordering is covered by
        // Out_of_order_arrival_with_header_first_still_completes_correctly below.
        var instruments = CreateMessage(snapshotId, "instruments", """{"total":21}""");
        var calculations = CreateMessage(snapshotId, "calculations", """{"pnl":100}""");
        var settings = CreateMessage(snapshotId, "settings", """{"rebalance":true}""");
        var header = CreateMessage(snapshotId, "header", HeaderJson);
        Track(instruments, calculations, settings, header);

        await handler.HandleAsync(instruments, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await handler.HandleAsync(calculations, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await handler.HandleAsync(settings, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await handler.HandleAsync(header, CancellationToken.None);

        var tracking = await LoadTrackingEntryAsync(snapshotId);
        var index = await LoadIndexEntryAsync(snapshotId);

        Assert.NotNull(tracking);
        Assert.Equal(SnapshotTrackingStatus.Complete, tracking!.Status);
        Assert.NotNull(tracking.CompletedAt);
        Assert.Equal(timeProvider.UtcNow.UtcDateTime, tracking.CompletedAt);
        Assert.Equal(4, tracking.ReceivedFiles.Count);

        AssertIndexEntryMatchesHeader(index, snapshotId, header, tracking);
    }

    [Fact]
    public async Task Redelivery_of_the_completing_message_produces_no_duplicate_index_row()
    {
        var snapshotId = NewSnapshotId();
        var timeProvider = new SteppingTimeProvider(StartTime);
        var handler = CreateHandler(timeProvider);

        var header = CreateMessage(snapshotId, "header", HeaderJson);
        var instruments = CreateMessage(snapshotId, "instruments", """{"total":21}""");
        var calculations = CreateMessage(snapshotId, "calculations", """{"pnl":100}""");
        var settings = CreateMessage(snapshotId, "settings", """{"rebalance":true}""");
        Track(header, instruments, calculations, settings);

        await handler.HandleAsync(header, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await handler.HandleAsync(instruments, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await handler.HandleAsync(calculations, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await handler.HandleAsync(settings, CancellationToken.None); // completes it

        var trackingAfterFirstCompletion = await LoadTrackingEntryAsync(snapshotId);
        Assert.NotNull(trackingAfterFirstCompletion);
        var completedAtAfterFirstCompletion = trackingAfterFirstCompletion!.CompletedAt;

        // Redeliver the completing message twice more. The handler's guard (tracking
        // already COMPLETE, not RECEIVING) means no header re-fetch, no index re-upsert
        // and no MarkComplete call happens at all on these redeliveries — see
        // SnapshotMessageHandler's write-order guard. Note: the reviewer flagged that
        // DisplayData lacks an EF Core ValueComparer, so *if* the guard ever did re-issue
        // the index UPSERT (e.g. redelivery arriving before MarkComplete persisted) EF
        // would re-issue an UPDATE with identical content — harmless noise, not something
        // this test needs to trigger or assert against.
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await handler.HandleAsync(settings, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await handler.HandleAsync(settings, CancellationToken.None);

        var tracking = await LoadTrackingEntryAsync(snapshotId);
        var index = await LoadIndexEntryAsync(snapshotId);

        Assert.NotNull(tracking);
        Assert.Equal(SnapshotTrackingStatus.Complete, tracking!.Status);
        Assert.Equal(completedAtAfterFirstCompletion, tracking.CompletedAt);

        AssertIndexEntryMatchesHeader(index, snapshotId, header, tracking);

        await using var context = new SnapshotWriterDbContext(_dbOptions);
        var indexRowCount = await context.SnapshotIndex.CountAsync(e => e.SnapshotId == snapshotId);
        Assert.Equal(1, indexRowCount);
    }

    [Fact]
    public async Task Redelivery_of_an_earlier_payload_after_completion_does_not_regress_status_or_duplicate_the_index_row()
    {
        var snapshotId = NewSnapshotId();
        var timeProvider = new SteppingTimeProvider(StartTime);
        var handler = CreateHandler(timeProvider);

        var header = CreateMessage(snapshotId, "header", HeaderJson);
        var instruments = CreateMessage(snapshotId, "instruments", """{"total":21}""");
        var calculations = CreateMessage(snapshotId, "calculations", """{"pnl":100}""");
        var settings = CreateMessage(snapshotId, "settings", """{"rebalance":true}""");
        Track(header, instruments, calculations, settings);

        await handler.HandleAsync(header, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await handler.HandleAsync(instruments, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await handler.HandleAsync(calculations, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await handler.HandleAsync(settings, CancellationToken.None); // completes it

        var trackingAfterCompletion = await LoadTrackingEntryAsync(snapshotId);
        Assert.NotNull(trackingAfterCompletion);
        var completedAt = trackingAfterCompletion!.CompletedAt;

        // Redeliver an EARLIER, non-completing payload (not the one that triggered
        // completion) well after the snapshot is already COMPLETE.
        timeProvider.Advance(TimeSpan.FromHours(1));
        await handler.HandleAsync(header, CancellationToken.None);

        var tracking = await LoadTrackingEntryAsync(snapshotId);

        Assert.NotNull(tracking);
        Assert.Equal(SnapshotTrackingStatus.Complete, tracking!.Status);
        Assert.Equal(completedAt, tracking.CompletedAt);
        Assert.Equal(4, tracking.ReceivedFiles.Count);

        await using var context = new SnapshotWriterDbContext(_dbOptions);
        var indexRowCount = await context.SnapshotIndex.CountAsync(e => e.SnapshotId == snapshotId);
        Assert.Equal(1, indexRowCount);
    }

    [Fact]
    public async Task Out_of_order_arrival_with_header_first_still_completes_correctly()
    {
        var snapshotId = NewSnapshotId();
        var timeProvider = new SteppingTimeProvider(StartTime);
        var handler = CreateHandler(timeProvider);

        // header arrives FIRST here (not last, as in Fourth_required_payload_completes_...
        // above) — proves the handler re-fetches header.json from blob at completion time
        // rather than depending on arrival order, per the ISnapshotBlobStore.ReadHeaderAsync
        // contract (never trust message.Payload).
        var header = CreateMessage(snapshotId, "header", HeaderJson);
        var instruments = CreateMessage(snapshotId, "instruments", """{"total":21}""");
        var calculations = CreateMessage(snapshotId, "calculations", """{"pnl":100}""");
        var settings = CreateMessage(snapshotId, "settings", """{"rebalance":true}""");
        Track(header, instruments, calculations, settings);

        await handler.HandleAsync(header, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await handler.HandleAsync(instruments, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await handler.HandleAsync(calculations, CancellationToken.None);

        // No index row yet — still missing settings.
        Assert.Null(await LoadIndexEntryAsync(snapshotId));

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await handler.HandleAsync(settings, CancellationToken.None); // completes it

        var tracking = await LoadTrackingEntryAsync(snapshotId);
        var index = await LoadIndexEntryAsync(snapshotId);

        Assert.NotNull(tracking);
        Assert.Equal(SnapshotTrackingStatus.Complete, tracking!.Status);
        AssertIndexEntryMatchesHeader(index, snapshotId, header, tracking);
    }

    [Fact]
    public async Task Payloads_published_across_a_month_boundary_land_under_one_root_folder_and_complete()
    {
        var snapshotId = NewSnapshotId();
        var firstArrival = new DateTimeOffset(2026, 5, 31, 23, 59, 58, TimeSpan.Zero);
        var timeProvider = new SteppingTimeProvider(firstArrival);
        var handler = CreateHandler(timeProvider);

        // Publish timestamps deliberately straddle the May/June 2026 boundary — before
        // the fix, per-message PublishedAt-derived paths split this snapshot across
        // month=05 and month=06 folders and completion could not find header.json.
        var header = CreateMessage(snapshotId, "header", HeaderJson,
            publishedAt: new DateTime(2026, 5, 31, 23, 59, 58, DateTimeKind.Utc));
        var instruments = CreateMessage(snapshotId, "instruments", """{"total":21}""",
            publishedAt: new DateTime(2026, 6, 1, 0, 0, 2, DateTimeKind.Utc));
        var calculations = CreateMessage(snapshotId, "calculations", """{"pnl":100}""",
            publishedAt: new DateTime(2026, 6, 1, 0, 0, 2, DateTimeKind.Utc));
        var settings = CreateMessage(snapshotId, "settings", """{"rebalance":true}""",
            publishedAt: new DateTime(2026, 6, 1, 0, 0, 2, DateTimeKind.Utc));
        Track(firstArrival, header, instruments, calculations, settings);

        await handler.HandleAsync(header, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(4)); // wall clock also crosses the boundary
        await handler.HandleAsync(instruments, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await handler.HandleAsync(calculations, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        // Completing message: ReadHeaderAsync must find header.json under the pinned
        // root — if any payload had landed elsewhere this throws and the test fails.
        await handler.HandleAsync(settings, CancellationToken.None);

        var tracking = await LoadTrackingEntryAsync(snapshotId);
        var index = await LoadIndexEntryAsync(snapshotId);

        Assert.NotNull(tracking);
        Assert.Equal(SnapshotTrackingStatus.Complete, tracking!.Status);

        // All four blobs exist under the SAME root folder — the tracking row's pinned one.
        var rootFolder = tracking.AdlsRootPath;
        Assert.Contains("year=2026/month=05", rootFolder);
        var blobNames = new List<string>();
        await foreach (var blobItem in _container.GetBlobsAsync(prefix: $"{rootFolder}/"))
        {
            blobNames.Add(blobItem.Name);
        }

        Assert.Equal(
            [
                $"{rootFolder}/calculations.json",
                $"{rootFolder}/header.json",
                $"{rootFolder}/instruments.json",
                $"{rootFolder}/settings.json",
            ],
            blobNames.OrderBy(name => name, StringComparer.Ordinal));

        Assert.NotNull(index);
        Assert.Equal(rootFolder, index!.AdlsPath);
    }

    [Fact]
    public async Task Unconfigured_snapshot_type_throws_and_writes_no_index_row()
    {
        var snapshotId = NewSnapshotId();
        var timeProvider = new SteppingTimeProvider(StartTime);
        var handler = CreateHandler(timeProvider);

        // "unknown_type" is deliberately absent from this project's appsettings.json
        // SnapshotConfig section — the completeness check runs (and can throw) on every
        // message while the tracking row is RECEIVING, not only on the "completing" one.
        var message = CreateMessage(snapshotId, "header", HeaderJson, snapshotType: "unknown_type");
        Track(message);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => handler.HandleAsync(message, CancellationToken.None));

        // Steps 1-2 (blob write, tracking upsert) are unconditional and already happened
        // before the completeness check that threw — per the strict write order, this is
        // expected: the exception propagates unchanged so no offset would ever be
        // committed by the real consumer, and redelivery would retry from the top.
        var tracking = await LoadTrackingEntryAsync(snapshotId);
        Assert.NotNull(tracking);
        Assert.Equal(SnapshotTrackingStatus.Receiving, tracking!.Status);

        var blob = _container.GetBlobClient($"{tracking.AdlsRootPath}/header.json");
        Assert.True(await blob.ExistsAsync());

        Assert.Null(await LoadIndexEntryAsync(snapshotId));
    }

    private static void AssertIndexEntryMatchesHeader(
        SnapshotIndexEntry? index,
        string snapshotId,
        SnapshotMessage headerMessage,
        SnapshotTrackingEntry tracking)
    {
        Assert.NotNull(index);
        Assert.Equal(snapshotId, index!.SnapshotId);
        Assert.Equal(headerMessage.AccountId, index.AccountId);
        Assert.Equal(tracking.AdlsRootPath, index.AdlsPath);
        Assert.Equal(tracking.FirstReceivedAt, index.SnapshotDate);

        Assert.Equal("ModelChange", index.EventType);
        Assert.Equal("MCCHM2EQ", index.DisplayData.Benchmark);
        Assert.Equal("CHF", index.DisplayData.BaseCcy);
        Assert.Equal("123456", index.DisplayData.ProgramId);
        Assert.Equal("15884", index.DisplayData.BatchId);
        Assert.Equal(4, index.DisplayData.NumOrders);
        Assert.Equal(0, index.DisplayData.PtcAlerts);
        Assert.Equal("Anna Miller", index.DisplayData.OrderApprovedBy);
        Assert.Equal(new DateTime(2026, 5, 15, 6, 10, 14, DateTimeKind.Utc), index.DisplayData.OrderApprovedAt);
        Assert.Equal("James Smith", index.DisplayData.OrderSentBy);
        Assert.Equal(new DateTime(2026, 5, 15, 6, 14, 22, DateTimeKind.Utc), index.DisplayData.OrderSentAt);
    }

    private SnapshotMessageHandler CreateHandler(TimeProvider timeProvider)
    {
        var blobStore = new AzureBlobSnapshotStore(Options.Create(_blobOptions));
        var factory = new SingleContextFactory(_dbOptions);
        var trackingStore = new SqlSnapshotTrackingStore(factory, timeProvider);
        var indexStore = new SqlSnapshotIndexStore(factory, timeProvider);
        var requiredFilesProvider = new SnapshotConfigRequiredFilesProvider(
            Options.Create(TestConfiguration.SnapshotConfig));

        return new SnapshotMessageHandler(
            blobStore,
            trackingStore,
            requiredFilesProvider,
            indexStore,
            timeProvider,
            NullLogger<SnapshotMessageHandler>.Instance);
    }

    private async Task<SnapshotTrackingEntry?> LoadTrackingEntryAsync(string snapshotId)
    {
        await using var context = new SnapshotWriterDbContext(_dbOptions);
        return await context.SnapshotTracking
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SnapshotId == snapshotId);
    }

    private async Task<SnapshotIndexEntry?> LoadIndexEntryAsync(string snapshotId)
    {
        await using var context = new SnapshotWriterDbContext(_dbOptions);
        return await context.SnapshotIndex
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SnapshotId == snapshotId);
    }

    private void Track(params SnapshotMessage[] messages) => Track(StartTime, messages);

    private void Track(DateTimeOffset pinnedArrival, params SnapshotMessage[] messages)
    {
        foreach (var message in messages)
        {
            // The handler pins each snapshot's root at the first payload's arrival time
            // (the test's SteppingTimeProvider start), so blob names are deterministic.
            _blobNamesToCleanUp.Add(
                SnapshotBlobPath.FullPath(SnapshotBlobPath.RootFolder(message, pinnedArrival), message.PayloadType));
            MarkForCleanup(message.SnapshotId);
        }
    }

    private void MarkForCleanup(string snapshotId)
    {
        if (!_snapshotIdsToCleanUp.Contains(snapshotId, StringComparer.Ordinal))
        {
            _snapshotIdsToCleanUp.Add(snapshotId);
        }
    }

    private static string NewSnapshotId() => $"it-cmp-{Guid.NewGuid():N}";

    private static SnapshotMessage CreateMessage(
        string snapshotId,
        string payloadType,
        string payloadJson,
        string snapshotType = "portfolio",
        DateTime? publishedAt = null)
    {
        using var payload = JsonDocument.Parse(payloadJson);
        return new SnapshotMessage
        {
            SnapshotId = snapshotId,
            AccountId = "00675442A",
            SnapshotType = snapshotType,
            PayloadType = payloadType,
            Stage = "PreTrade",
            PublishedAt = publishedAt ?? new DateTime(2026, 5, 22, 6, 10, 14, DateTimeKind.Utc),
            PublishedBy = "PortfolioCalculation",
            SchemaVersion = "1.0",
            Payload = payload.RootElement.Clone(),
        };
    }

    /// <summary>
    /// <see cref="IDbContextFactory{TContext}"/> adapter over a fixed <see cref="DbContextOptions{TContext}"/>
    /// so tests can wire <see cref="SqlSnapshotTrackingStore"/> / <see cref="SqlSnapshotIndexStore"/>
    /// exactly the way the Worker composition root does (a factory, not a shared
    /// long-lived context).
    /// </summary>
    private sealed class SingleContextFactory(DbContextOptions<SnapshotWriterDbContext> options)
        : IDbContextFactory<SnapshotWriterDbContext>
    {
        public SnapshotWriterDbContext CreateDbContext() => new(options);

        public Task<SnapshotWriterDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new SnapshotWriterDbContext(options));
    }

    /// <summary>
    /// Deterministic <see cref="TimeProvider"/> so tests can assert exact
    /// completed_at / first_received_at values instead of "close to now" ranges.
    /// </summary>
    private sealed class SteppingTimeProvider(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public override DateTimeOffset GetUtcNow() => UtcNow;

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
