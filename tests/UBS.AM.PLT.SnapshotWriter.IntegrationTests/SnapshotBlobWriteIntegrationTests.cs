using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UBS.AM.PLT.SnapshotWriter.Application;
using UBS.AM.PLT.SnapshotWriter.Application.Interfaces.Infrastructure;
using UBS.AM.PLT.SnapshotWriter.Domain;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Blob;
using Xunit;

namespace UBS.AM.PLT.SnapshotWriter.IntegrationTests;

/// <summary>
/// Mode A (in-process) integration tests for slice 2 (blob write), per AGENTS.md and
/// the brief's acceptance criteria. Builds <see cref="SnapshotMessage"/> envelopes in
/// code and feeds them directly into <see cref="SnapshotMessageHandler"/> wired to the
/// real <see cref="AzureBlobSnapshotStore"/> against a live Azurite instance — Kafka is
/// never involved. Requires Azurite running locally
/// (<c>pwsh ./tools/azurite-local.ps1 -Up</c>); connection details come from
/// appsettings.json / environment variables (see <see cref="TestConfiguration"/>), never
/// hardcoded.
///
/// Each test uses a unique snapshotId (a fresh GUID) so tests never collide with each
/// other or with data left behind by other tooling (e.g. the Mode B live worker run),
/// and each test deletes the blobs it wrote in <see cref="DisposeAsync"/> so the suite
/// leaves no residue in the shared Azurite container.
/// </summary>
public sealed class SnapshotBlobWriteIntegrationTests : IAsyncLifetime
{
    /// <summary>
    /// Fixed arrival time (via <see cref="FixedTimeProvider"/>) so the handler pins a
    /// deterministic year=2026/month=05 root folder for every message in this suite.
    /// </summary>
    private static readonly DateTimeOffset ArrivalTime = new(2026, 5, 22, 6, 10, 14, TimeSpan.Zero);

    private readonly BlobContainerClient _container;
    private readonly SnapshotMessageHandler _handler;
    private readonly InMemorySnapshotTrackingStore _trackingStore = new();
    private readonly List<string> _blobNamesToCleanUp = [];

    public SnapshotBlobWriteIntegrationTests()
    {
        var options = TestConfiguration.BlobStorageOptions;
        _container = new BlobContainerClient(options.ConnectionString, options.ContainerName);
        var blobStore = new AzureBlobSnapshotStore(Options.Create(options));
        _handler = new SnapshotMessageHandler(
            blobStore,
            _trackingStore,
            new PortfolioRequiredFilesProvider(),
            new NoOpSnapshotIndexStore(),
            new FixedTimeProvider(ArrivalTime),
            NullLogger<SnapshotMessageHandler>.Instance);
    }

    /// <summary>
    /// Keeps these slice-2 tests blob-only: tracking (step 2) is satisfied in memory so
    /// no SQL Server is required. SQL-asserting integration tests are a separate suite.
    /// Mirrors the real store's root pinning (first upsert wins, <see cref="GetRootPathAsync"/>
    /// reads it back) so the handler's root reuse works here, but each upsert reports only
    /// the single filename just "received" (never accumulated across calls), so the
    /// completeness check (steps 3-4, slice 4) never sees more than one received file and
    /// is never satisfied here regardless of the required list.
    /// </summary>
    private sealed class InMemorySnapshotTrackingStore : ISnapshotTrackingStore
    {
        public Dictionary<string, string> RootPathsBySnapshotId { get; } = new(StringComparer.Ordinal);

        public Task<SnapshotTrackingEntry> UpsertReceivedAsync(
            SnapshotMessage message,
            string adlsRootPath,
            CancellationToken cancellationToken)
        {
            RootPathsBySnapshotId.TryAdd(message.SnapshotId, adlsRootPath);

            return Task.FromResult(new SnapshotTrackingEntry
            {
                SnapshotId = message.SnapshotId,
                AccountId = message.AccountId,
                SnapshotType = message.SnapshotType,
                AdlsRootPath = adlsRootPath,
                ReceivedFiles = [SnapshotBlobPath.FileName(message.PayloadType)],
                Status = SnapshotTrackingStatus.Receiving,
                FirstReceivedAt = message.PublishedAt,
                LastUpdatedAt = message.PublishedAt,
            });
        }

        public Task<string?> GetRootPathAsync(string snapshotId, CancellationToken cancellationToken)
            => Task.FromResult(RootPathsBySnapshotId.TryGetValue(snapshotId, out var rootPath) ? rootPath : null);

        public Task MarkCompleteAsync(string snapshotId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    /// <summary>Real portfolio required-file list (design §4) — never satisfied here, see <see cref="NoOpSnapshotTrackingStore"/>.</summary>
    private sealed class PortfolioRequiredFilesProvider : IRequiredFilesProvider
    {
        public IReadOnlySet<string> GetRequiredFiles(string snapshotType)
            => new HashSet<string> { "header.json", "instruments.json", "calculations.json", "settings.json" };
    }

    /// <summary>Never invoked in this suite (see <see cref="InMemorySnapshotTrackingStore"/>); present only to satisfy DI.</summary>
    private sealed class NoOpSnapshotIndexStore : ISnapshotIndexStore
    {
        public Task UpsertAsync(SnapshotIndexEntry entry, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    /// <summary>Deterministic <see cref="TimeProvider"/> so pinned root folders are exact, assertable paths.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var blobName in _blobNamesToCleanUp)
        {
            await _container.DeleteBlobIfExistsAsync(blobName);
        }
    }

    [Fact]
    public async Task HandleAsync_writes_blob_at_the_exact_expected_path_with_correct_content_and_content_type()
    {
        var snapshotId = NewSnapshotId();
        var message = CreateMessage(snapshotId, "instruments", """{"total":21,"equities":[]}""");
        Track(message);

        await _handler.HandleAsync(message, CancellationToken.None);

        var expectedBlobName =
            $"portfolio_snapshots/year=2026/month=05/accountId=00675442A/snapshotId={snapshotId}/instruments.json";
        var blob = _container.GetBlobClient(expectedBlobName);

        Assert.True(await blob.ExistsAsync());

        var download = await blob.DownloadContentAsync();
        Assert.Equal("""{"total":21,"equities":[]}""", download.Value.Content.ToString());
        Assert.Equal("application/json", download.Value.Details.ContentType);
    }

    [Fact]
    public async Task HandleAsync_is_idempotent_under_redelivery_of_the_same_message()
    {
        var snapshotId = NewSnapshotId();
        var message = CreateMessage(snapshotId, "instruments", """{"total":21}""");
        Track(message);

        await _handler.HandleAsync(message, CancellationToken.None);
        await _handler.HandleAsync(message, CancellationToken.None);
        await _handler.HandleAsync(message, CancellationToken.None);

        var blobName = BlobNameFor(message);
        var blobsUnderPath = await ListBlobNamesAsync(blobName);

        Assert.Single(blobsUnderPath);

        var blob = _container.GetBlobClient(blobName);
        var download = await blob.DownloadContentAsync();
        Assert.Equal("""{"total":21}""", download.Value.Content.ToString());
    }

    [Fact]
    public async Task HandleAsync_redelivery_with_identical_payload_overwrites_with_identical_content()
    {
        var snapshotId = NewSnapshotId();
        var firstDelivery = CreateMessage(snapshotId, "calculations", """{"pnl":100}""");
        Track(firstDelivery);

        await _handler.HandleAsync(firstDelivery, CancellationToken.None);

        // Redelivery of the same logical message: same envelope, same payload content.
        var redelivered = CreateMessage(snapshotId, "calculations", """{"pnl":100}""");

        await _handler.HandleAsync(redelivered, CancellationToken.None);

        var blob = _container.GetBlobClient(BlobNameFor(firstDelivery));
        var download = await blob.DownloadContentAsync();
        Assert.Equal("""{"pnl":100}""", download.Value.Content.ToString());
    }

    [Fact]
    public async Task HandleAsync_lands_multiple_payload_types_of_one_snapshot_under_the_same_root_folder()
    {
        var snapshotId = NewSnapshotId();
        var header = CreateMessage(snapshotId, "header", """{"eventType":"ModelChange"}""");
        var instruments = CreateMessage(snapshotId, "instruments", """{"total":21}""");
        var calculations = CreateMessage(snapshotId, "calculations", """{"pnl":100}""");
        var settings = CreateMessage(snapshotId, "settings", """{"rebalance":true}""");
        Track(header, instruments, calculations, settings);

        await _handler.HandleAsync(header, CancellationToken.None);
        await _handler.HandleAsync(instruments, CancellationToken.None);
        await _handler.HandleAsync(calculations, CancellationToken.None);
        await _handler.HandleAsync(settings, CancellationToken.None);

        // The root all four blobs must share is the one the handler actually pinned
        // (visible via the tracking store), not one recomputed from the messages.
        var rootFolder = _trackingStore.RootPathsBySnapshotId[snapshotId];
        var blobNames = await ListBlobNamesAsync(rootFolder);

        Assert.Equal(
            [
                $"{rootFolder}/calculations.json",
                $"{rootFolder}/header.json",
                $"{rootFolder}/instruments.json",
                $"{rootFolder}/settings.json",
            ],
            blobNames.OrderBy(name => name, StringComparer.Ordinal));

        var headerBlob = await _container.GetBlobClient($"{rootFolder}/header.json").DownloadContentAsync();
        Assert.Equal("""{"eventType":"ModelChange"}""", headerBlob.Value.Content.ToString());
    }

    private async Task<List<string>> ListBlobNamesAsync(string prefix)
    {
        var names = new List<string>();
        await foreach (var blobItem in _container.GetBlobsAsync(prefix: prefix))
        {
            names.Add(blobItem.Name);
        }

        return names;
    }

    private void Track(params SnapshotMessage[] messages)
    {
        foreach (var message in messages)
        {
            _blobNamesToCleanUp.Add(BlobNameFor(message));
        }
    }

    /// <summary>Expected blob name for a message in this suite — the handler pins the root at <see cref="ArrivalTime"/>.</summary>
    private static string BlobNameFor(SnapshotMessage message)
        => SnapshotBlobPath.FullPath(SnapshotBlobPath.RootFolder(message, ArrivalTime), message.PayloadType);

    private static string NewSnapshotId() => $"it-{Guid.NewGuid():N}";

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
}
