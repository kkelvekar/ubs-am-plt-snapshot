using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UBS.AM.PLT.SnapshotWriter.Application;
using UBS.AM.PLT.SnapshotWriter.Application.Interfaces.Infrastructure;
using UBS.AM.PLT.SnapshotWriter.Domain;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Blob;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence;
using Xunit;

namespace UBS.AM.PLT.SnapshotWriter.IntegrationTests;

/// <summary>
/// Mode A (in-process) integration tests for slice 3 (tracking upsert — write-order step
/// 2), per AGENTS.md and the brief's acceptance criteria. Builds <see cref="SnapshotMessage"/>
/// envelopes in code and feeds them directly into <see cref="SnapshotMessageHandler"/>
/// wired to the real <see cref="AzureBlobSnapshotStore"/> (Azurite) and the real
/// <see cref="SqlSnapshotTrackingStore"/> (local SQL Server) — Kafka is never involved.
/// Requires Azurite (<c>pwsh ./tools/azurite-local.ps1 -Up</c>) and SQL Server
/// (<c>pwsh ./tools/sqlserver-local.ps1 -Up</c>) running locally; connection details come
/// from appsettings.json / environment variables (see <see cref="TestConfiguration"/>),
/// never hardcoded.
///
/// Each test uses a unique snapshotId (a fresh GUID) so tests never collide with each
/// other or with data left behind by other tooling (e.g. the Mode B live worker run), and
/// each test deletes the blobs and tracking rows it wrote in <see cref="DisposeAsync"/> so
/// the suite leaves no residue in the shared Azurite/SQL Server containers. If SQL Server
/// or Azurite is unreachable the underlying client calls throw and the test fails clearly
/// with that exception — there is no silent skip.
/// </summary>
public sealed class SnapshotTrackingIntegrationTests : IAsyncLifetime
{
    private readonly BlobContainerClient _container;
    private readonly BlobStorageOptions _blobOptions;
    private readonly DbContextOptions<SnapshotWriterDbContext> _dbOptions;
    private readonly List<string> _blobNamesToCleanUp = [];
    private readonly List<string> _snapshotIdsToCleanUp = [];

    public SnapshotTrackingIntegrationTests()
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
        }
    }

    [Fact]
    public async Task First_payload_inserts_a_receiving_row_with_the_expected_shape()
    {
        var snapshotId = NewSnapshotId();
        var timeProvider = new SteppingTimeProvider(new DateTimeOffset(2026, 5, 22, 6, 10, 14, TimeSpan.Zero));
        var handler = CreateHandler(timeProvider);
        var message = CreateMessage(snapshotId, "instruments", """{"total":21}""");
        Track(message);

        await handler.HandleAsync(message, CancellationToken.None);

        var row = await LoadTrackingEntryAsync(snapshotId);

        Assert.NotNull(row);
        Assert.Equal(SnapshotTrackingStatus.Receiving, row!.Status);
        Assert.Equal(SnapshotBlobPath.RootFolder(message), row.AdlsRootPath);
        Assert.Equal(["instruments.json"], row.ReceivedFiles);
        Assert.Equal(timeProvider.UtcNow.UtcDateTime, row.FirstReceivedAt);
        Assert.Equal(row.FirstReceivedAt, row.LastUpdatedAt);
        Assert.Null(row.MissingFiles);
        Assert.Null(row.CompletedAt);
        Assert.Null(row.DeclaredFailedAt);
        Assert.False(row.Alerted);

        // Blob + row consistency: the row's root path must actually contain the blob just written.
        var blob = _container.GetBlobClient($"{row.AdlsRootPath}/instruments.json");
        Assert.True(await blob.ExistsAsync());
    }

    [Fact]
    public async Task Redelivery_of_the_same_message_is_idempotent_and_produces_no_duplicate_entries()
    {
        var snapshotId = NewSnapshotId();
        var timeProvider = new SteppingTimeProvider(new DateTimeOffset(2026, 5, 22, 6, 10, 14, TimeSpan.Zero));
        var handler = CreateHandler(timeProvider);
        var message = CreateMessage(snapshotId, "instruments", """{"total":21}""");
        Track(message);

        await handler.HandleAsync(message, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await handler.HandleAsync(message, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await handler.HandleAsync(message, CancellationToken.None);

        await using var context = new SnapshotWriterDbContext(_dbOptions);
        var rows = await context.SnapshotTracking
            .Where(e => e.SnapshotId == snapshotId)
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal(["instruments.json"], row.ReceivedFiles);
    }

    [Fact]
    public async Task Subsequent_payload_appends_the_filename_and_advances_last_updated_at_only()
    {
        var snapshotId = NewSnapshotId();
        var firstReceivedAt = new DateTimeOffset(2026, 5, 22, 6, 10, 14, TimeSpan.Zero);
        var secondReceivedAt = firstReceivedAt.AddMinutes(3);
        var timeProvider = new SteppingTimeProvider(firstReceivedAt);
        var handler = CreateHandler(timeProvider);

        var header = CreateMessage(snapshotId, "header", """{"eventType":"ModelChange"}""");
        var instruments = CreateMessage(snapshotId, "instruments", """{"total":21}""");
        Track(header, instruments);

        await handler.HandleAsync(header, CancellationToken.None);
        timeProvider.Set(secondReceivedAt);
        await handler.HandleAsync(instruments, CancellationToken.None);

        var row = await LoadTrackingEntryAsync(snapshotId);

        Assert.NotNull(row);
        Assert.Equal(["header.json", "instruments.json"], row!.ReceivedFiles);
        Assert.Equal(SnapshotTrackingStatus.Receiving, row.Status);
        Assert.Equal(SnapshotBlobPath.RootFolder(header), row.AdlsRootPath);
        Assert.Equal(firstReceivedAt.UtcDateTime, row.FirstReceivedAt);
        Assert.Equal(secondReceivedAt.UtcDateTime, row.LastUpdatedAt);
    }

    [Fact]
    public async Task Out_of_order_payload_arrival_produces_the_same_row_regardless_of_arrival_order()
    {
        var snapshotId = NewSnapshotId();
        var timeProvider = new SteppingTimeProvider(new DateTimeOffset(2026, 5, 22, 6, 10, 14, TimeSpan.Zero));
        var handler = CreateHandler(timeProvider);

        var header = CreateMessage(snapshotId, "header", """{"eventType":"ModelChange"}""");
        var instruments = CreateMessage(snapshotId, "instruments", """{"total":21}""");
        var calculations = CreateMessage(snapshotId, "calculations", """{"pnl":100}""");
        var settings = CreateMessage(snapshotId, "settings", """{"rebalance":true}""");
        Track(header, instruments, calculations, settings);

        // Arrive out of the "natural" header-first order.
        await handler.HandleAsync(settings, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await handler.HandleAsync(instruments, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await handler.HandleAsync(header, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await handler.HandleAsync(calculations, CancellationToken.None);

        var row = await LoadTrackingEntryAsync(snapshotId);

        Assert.NotNull(row);
        Assert.Equal(SnapshotTrackingStatus.Receiving, row!.Status);
        Assert.Equal(
            ["calculations.json", "header.json", "instruments.json", "settings.json"],
            row.ReceivedFiles.OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(4, row.ReceivedFiles.Count);
    }

    [Fact]
    public async Task Status_is_never_regressed_by_redelivery_once_a_row_is_complete()
    {
        var snapshotId = NewSnapshotId();
        var seededAt = new DateTime(2026, 5, 22, 6, 10, 14, DateTimeKind.Utc);
        var message = CreateMessage(snapshotId, "instruments", """{"total":21}""");
        Track(message);
        MarkForCleanup(snapshotId);

        // Seed a COMPLETE row directly, bypassing the store — simulating a snapshot whose
        // completeness check (a later slice) has already run.
        await using (var seedContext = new SnapshotWriterDbContext(_dbOptions))
        {
            seedContext.SnapshotTracking.Add(new SnapshotTrackingEntry
            {
                SnapshotId = snapshotId,
                AccountId = message.AccountId,
                SnapshotType = message.SnapshotType,
                AdlsRootPath = SnapshotBlobPath.RootFolder(message),
                ReceivedFiles = ["header.json", "instruments.json", "calculations.json", "settings.json"],
                Status = SnapshotTrackingStatus.Complete,
                FirstReceivedAt = seededAt,
                LastUpdatedAt = seededAt,
                CompletedAt = seededAt,
            });
            await seedContext.SaveChangesAsync();
        }

        var timeProvider = new SteppingTimeProvider(seededAt.AddHours(1));
        var handler = CreateHandler(timeProvider);

        // Redelivery of an already-received payload for an already-COMPLETE snapshot.
        await handler.HandleAsync(message, CancellationToken.None);

        var row = await LoadTrackingEntryAsync(snapshotId);

        Assert.NotNull(row);
        Assert.Equal(SnapshotTrackingStatus.Complete, row!.Status);
        Assert.Equal(SnapshotBlobPath.RootFolder(message), row.AdlsRootPath);
        Assert.Equal(seededAt, row.FirstReceivedAt);
        Assert.Equal(
            ["header.json", "instruments.json", "calculations.json", "settings.json"],
            row.ReceivedFiles);
    }

    private SnapshotMessageHandler CreateHandler(TimeProvider timeProvider)
    {
        var blobStore = new AzureBlobSnapshotStore(Options.Create(_blobOptions));
        var factory = new SingleContextFactory(_dbOptions);
        var trackingStore = new SqlSnapshotTrackingStore(factory, timeProvider);
        return new SnapshotMessageHandler(
            blobStore,
            trackingStore,
            new NeverCompleteRequiredFilesProvider(),
            new NoOpSnapshotIndexStore(),
            NullLogger<SnapshotMessageHandler>.Instance);
    }

    /// <summary>
    /// This suite exercises only the tracking upsert (slice 3, write-order step 2); the
    /// real <see cref="SqlSnapshotTrackingStore"/> here does accumulate received_files
    /// across calls, so a real required-file list could actually be satisfied by some
    /// scenarios (e.g. the out-of-order-arrival test delivers all four portfolio files)
    /// and would then exercise the completeness/index path added in slice 4 — out of
    /// scope for this file. Requiring a filename no test ever sends keeps completeness
    /// (and therefore the index write and the COMPLETE status flip) permanently
    /// unreachable here, preserving this suite's original tracking-only assertions.
    /// Slice 4's own completeness/index behaviour is covered by
    /// <see cref="SnapshotMessageHandlerTests"/> (unit) — a dedicated Mode A integration
    /// test for the full write order is tester-e2e's to add.
    /// </summary>
    private sealed class NeverCompleteRequiredFilesProvider : IRequiredFilesProvider
    {
        public IReadOnlySet<string> GetRequiredFiles(string snapshotType)
            => new HashSet<string> { "__never_delivered_by_this_suite__.json" };
    }

    private sealed class NoOpSnapshotIndexStore : ISnapshotIndexStore
    {
        public Task UpsertAsync(SnapshotIndexEntry entry, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private async Task<SnapshotTrackingEntry?> LoadTrackingEntryAsync(string snapshotId)
    {
        await using var context = new SnapshotWriterDbContext(_dbOptions);
        return await context.SnapshotTracking
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SnapshotId == snapshotId);
    }

    private void Track(params SnapshotMessage[] messages)
    {
        foreach (var message in messages)
        {
            _blobNamesToCleanUp.Add(SnapshotBlobPath.FullPath(message));
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

    private static string NewSnapshotId() => $"it-trk-{Guid.NewGuid():N}";

    private static SnapshotMessage CreateMessage(string snapshotId, string payloadType, string payloadJson)
    {
        using var payload = JsonDocument.Parse(payloadJson);
        return new SnapshotMessage
        {
            SnapshotId = snapshotId,
            AccountId = "00675442A",
            SnapshotType = "portfolio",
            PayloadType = payloadType,
            Stage = "PreTrade",
            PublishedAt = new DateTime(2026, 5, 22, 6, 10, 14, DateTimeKind.Utc),
            PublishedBy = "PortfolioCalculation",
            SchemaVersion = "1.0",
            Payload = payload.RootElement.Clone(),
        };
    }

    /// <summary>
    /// <see cref="IDbContextFactory{TContext}"/> adapter over a fixed <see cref="DbContextOptions{TContext}"/>
    /// so tests can wire <see cref="SqlSnapshotTrackingStore"/> exactly the way the Worker
    /// composition root does (a factory, not a shared long-lived context).
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
    /// first_received_at / last_updated_at values instead of "close to now" ranges.
    /// </summary>
    private sealed class SteppingTimeProvider(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public override DateTimeOffset GetUtcNow() => UtcNow;

        public void Advance(TimeSpan by) => UtcNow += by;

        public void Set(DateTimeOffset value) => UtcNow = value;
    }
}
