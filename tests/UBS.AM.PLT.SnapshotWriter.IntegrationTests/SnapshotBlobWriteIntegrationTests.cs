using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UBS.AM.PLT.SnapshotWriter.Application;
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
    private readonly BlobContainerClient _container;
    private readonly SnapshotMessageHandler _handler;
    private readonly List<string> _blobNamesToCleanUp = [];

    public SnapshotBlobWriteIntegrationTests()
    {
        var options = TestConfiguration.BlobStorageOptions;
        _container = new BlobContainerClient(options.ConnectionString, options.ContainerName);
        var blobStore = new AzureBlobSnapshotStore(Options.Create(options));
        _handler = new SnapshotMessageHandler(blobStore, NullLogger<SnapshotMessageHandler>.Instance);
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

        var blobName = SnapshotBlobPath.FullPath(message);
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

        var blob = _container.GetBlobClient(SnapshotBlobPath.FullPath(firstDelivery));
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

        var rootFolder = SnapshotBlobPath.RootFolder(header);
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
            _blobNamesToCleanUp.Add(SnapshotBlobPath.FullPath(message));
        }
    }

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
