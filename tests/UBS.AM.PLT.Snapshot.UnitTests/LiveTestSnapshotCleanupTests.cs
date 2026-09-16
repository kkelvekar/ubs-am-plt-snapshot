using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Application.Features.LiveTestCleanup;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Infrastructure.Adls;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql;

namespace UBS.AM.PLT.Snapshot.UnitTests;

public sealed class LiveTestSnapshotCleanupTests
{
    private const string Id = "live-test-0123456789abcdef0123456789abcdef";
    private const string Root = "portfolio_snapshots/year=2026/month=09/accountId=00675442A/snapshotId=" + Id;
    private static readonly LiveTestSnapshotLocation Location = new(Id, "00675442A", Root);

    [Theory]
    [InlineData("corr98765")]
    [InlineData("it-test")]
    [InlineData("live-test-")]
    [InlineData(Id + "-extra")]
    [InlineData(Id + "\n")]
    [InlineData("LIVE-TEST-0123456789abcdef0123456789abcdef")]
    public async Task Non_test_ids_are_refused_before_any_deletion(string snapshotId)
    {
        var database = new Mock<ILiveTestSnapshotCleanupStore>(MockBehavior.Strict);
        var blobs = new Mock<ILiveTestSnapshotBlobCleanup>(MockBehavior.Strict);
        database.Setup(store => store.ListAsync(default)).ReturnsAsync([Location, new(snapshotId, "00675442A", "")]);
        blobs.Setup(store => store.ListAsync(default)).ReturnsAsync([]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(database, blobs).DeleteAsync(default));
    }

    [Theory]
    [InlineData("portfolio_snapshots/")]
    [InlineData(Root + "/")]
    [InlineData(Root + "/../snapshotId=corr98765")]
    [InlineData("portfolio_snapshots/year=2026/month=09/accountId=OTHER/snapshotId=" + Id)]
    public async Task Unsafe_and_mismatched_storage_paths_are_refused(string root)
    {
        var adapter = new AzureBlobLiveTestSnapshotCleanup(new Mock<BlobContainerClient>(MockBehavior.Strict).Object);
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.DeleteAsync(Location with { RootPath = root }, default));
    }

    [Fact]
    public async Task Deletes_storage_before_sql_deduplicates_paths_and_includes_orphans_and_rejected_rows()
    {
        var orphanId = LiveTestSnapshot.NewId();
        var orphan = Location with { SnapshotId = orphanId, RootPath = Root.Replace(Id, orphanId, StringComparison.Ordinal) };
        var rejected = new LiveTestSnapshotLocation(LiveTestSnapshot.NewId(), "invalid/account", "");
        var database = new Mock<ILiveTestSnapshotCleanupStore>(MockBehavior.Strict);
        var blobs = new Mock<ILiveTestSnapshotBlobCleanup>(MockBehavior.Strict);
        database.Setup(store => store.ListAsync(default)).ReturnsAsync([Location, Location, rejected]);
        blobs.Setup(store => store.ListAsync(default)).ReturnsAsync([Location, orphan]);
        var sequence = new MockSequence();
        blobs.InSequence(sequence).Setup(store => store.DeleteAsync(Location, default)).ReturnsAsync(6);
        database.InSequence(sequence).Setup(store => store.DeleteAsync(Id, default)).ReturnsAsync(2);
        database.InSequence(sequence).Setup(store => store.DeleteAsync(rejected.SnapshotId, default)).ReturnsAsync(1);
        blobs.InSequence(sequence).Setup(store => store.DeleteAsync(orphan, default)).ReturnsAsync(1);
        database.InSequence(sequence).Setup(store => store.DeleteAsync(orphanId, default)).ReturnsAsync(0);
        Assert.Equal(new LiveTestCleanupResult(3, 7, 3), await Service(database, blobs).DeleteAsync(default));
        blobs.Verify(store => store.DeleteAsync(Location, default), Times.Once);
    }

    [Fact]
    public async Task Storage_failure_preserves_sql_for_retry()
    {
        var database = new Mock<ILiveTestSnapshotCleanupStore>(MockBehavior.Strict);
        var blobs = new Mock<ILiveTestSnapshotBlobCleanup>(MockBehavior.Strict);
        database.Setup(store => store.ListAsync(default)).ReturnsAsync([Location]);
        blobs.Setup(store => store.ListAsync(default)).ReturnsAsync([]);
        blobs.Setup(store => store.DeleteAsync(Location, default)).ThrowsAsync(new IOException("Storage unavailable"));
        await Assert.ThrowsAsync<IOException>(() => Service(database, blobs).DeleteAsync(default));
        database.Verify(store => store.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Repeated_cleanup_of_empty_stores_succeeds()
    {
        var database = new Mock<ILiveTestSnapshotCleanupStore>(MockBehavior.Strict);
        var blobs = new Mock<ILiveTestSnapshotBlobCleanup>(MockBehavior.Strict);
        database.Setup(store => store.ListAsync(default)).ReturnsAsync([]);
        blobs.Setup(store => store.ListAsync(default)).ReturnsAsync([]);
        var service = Service(database, blobs);
        Assert.Equal(new LiveTestCleanupResult(0, 0, 0), await service.DeleteAsync(default));
        Assert.Equal(new LiveTestCleanupResult(0, 0, 0), await service.DeleteAsync(default));
    }

    [Fact]
    public async Task Blob_discovery_selects_only_exact_test_paths()
    {
        var container = new Mock<BlobContainerClient>(MockBehavior.Strict);
        container.Setup(client => client.GetBlobsAsync(BlobTraits.None, BlobStates.None, LiveTestSnapshot.StoragePrefix, default))
            .Returns(Blobs(Root + "/header.json", Root, Root.Replace(Id, "corr98765") + "/header.json",
                Root + "-extra/header.json", Root + "/nested/header.json"));
        Assert.Equal([Location], await new AzureBlobLiveTestSnapshotCleanup(container.Object).ListAsync(default));
    }

    [Fact]
    public async Task Blob_deletion_uses_snapshot_boundary_and_deletes_empty_adls_directory_last()
    {
        var container = new Mock<BlobContainerClient>(MockBehavior.Strict);
        var sequence = new MockSequence();
        container.InSequence(sequence).Setup(client => client.GetBlobsAsync(BlobTraits.None, BlobStates.None, Root + "/", default))
            .Returns(Blobs(Root + "/header.json"));
        container.InSequence(sequence).Setup(client => client.DeleteBlobIfExistsAsync(Root + "/header.json", DeleteSnapshotsOption.IncludeSnapshots, null, default))
            .ReturnsAsync(Response.FromValue(true, Mock.Of<Response>()));
        container.InSequence(sequence).Setup(client => client.DeleteBlobIfExistsAsync(Root, DeleteSnapshotsOption.None, null, default))
            .ReturnsAsync(Response.FromValue(true, Mock.Of<Response>()));
        Assert.Equal(1, await new AzureBlobLiveTestSnapshotCleanup(container.Object).DeleteAsync(Location, default));
        container.VerifyAll();
    }

    [Fact]
    public async Task Sql_refuses_ordinary_ids_without_opening_connection()
    {
        var factory = new Mock<Microsoft.EntityFrameworkCore.IDbContextFactory<SnapshotDbContext>>(MockBehavior.Strict);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SqlLiveTestSnapshotCleanupStore(factory.Object).DeleteAsync("corr98765", default));
    }

    [Fact]
    public void Generated_test_ids_are_unique_and_fit_wire_contract()
    {
        var ids = Enumerable.Range(0, 100).Select(_ => LiveTestSnapshot.NewId()).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(ids, id => { Assert.True(LiveTestSnapshot.IsTestId(id)); Assert.True(id.Length <= 50); });
    }

    private static AsyncPageable<BlobItem> Blobs(params string[] names)
        => AsyncPageable<BlobItem>.FromPages([
            Page<BlobItem>.FromValues(names.Select(name => BlobsModelFactory.BlobItem(name: name)).ToArray(), null, Mock.Of<Response>())]);

    private static LiveTestSnapshotCleanup Service(Mock<ILiveTestSnapshotCleanupStore> database, Mock<ILiveTestSnapshotBlobCleanup> blobs)
        => new(database.Object, blobs.Object, NullLogger<LiveTestSnapshotCleanup>.Instance);
}
