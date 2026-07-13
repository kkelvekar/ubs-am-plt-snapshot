using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace UBS.AM.PLT.SnapshotWriter.IntegrationTests;

/// <summary>
/// Per-test-instance base (xunit creates a fresh instance per test method) providing
/// snapshotId generation and surgical post-test cleanup against the REAL shared dev
/// Azure resources. Cleanup deletes only exact, explicitly registered <c>it-</c>-prefixed
/// ids — never a LIKE pattern, never a blanket delete — so pre-existing rows from earlier
/// live testing (e.g. corrTCA*/corrTCC*) can never be touched. Cleanup failures propagate
/// loudly: orphaned test data should be visible, not hidden.
/// </summary>
public abstract class IntegrationTestBase : IAsyncLifetime
{
    private readonly List<string> _snapshotIds = [];

    protected IntegrationTestBase(SnapshotWriterFixture fixture)
    {
        Fixture = fixture;
    }

    protected SnapshotWriterFixture Fixture { get; }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (!Fixture.Configuration.GetValue("IntegrationTestSettings:CleanupAfterTest", true))
        {
            return;
        }

        foreach (var snapshotId in _snapshotIds)
        {
            await CleanupSnapshotAsync(snapshotId);
        }
    }

    /// <summary>New unique snapshotId carrying the mandatory <c>it-</c> test marker.</summary>
    protected string NewSnapshotId(string testName)
    {
        var snapshotId = $"it-{testName}-{Guid.NewGuid():N}";
        RegisterSnapshotId(snapshotId);
        return snapshotId;
    }

    /// <summary>
    /// Tracks a snapshotId for post-test cleanup. Call immediately after generating the
    /// id, before sending any message, so cleanup fires even if the test fails partway.
    /// </summary>
    protected void RegisterSnapshotId(string snapshotId)
    {
        // Hard safety guardrail: this project deletes from a real shared dev database,
        // so only ids unmistakably created by this test run may ever be registered.
        if (!snapshotId.StartsWith("it-", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Refusing to track snapshotId '{snapshotId}' for cleanup: integration-test ids must start with 'it-'.",
                nameof(snapshotId));
        }

        _snapshotIds.Add(snapshotId);
    }

    private async Task CleanupSnapshotAsync(string snapshotId)
    {
        await using var context = await Fixture.DbContextFactory.CreateDbContextAsync();

        // Blob cleanup needs the root path from tracking; a test that only exercised a
        // failure path may have created no row (and therefore no blobs).
        var rootPath = await context.SnapshotTracking
            .AsNoTracking()
            .Where(e => e.SnapshotId == snapshotId)
            .Select(e => e.AdlsRootPath)
            .SingleOrDefaultAsync();

        if (rootPath is not null)
        {
            // The account has a hierarchical namespace (ADLS Gen2), so the flat listing
            // also returns directory entries, and a directory cannot be deleted while it
            // still has children — delete deepest-first so files go before their folders.
            var blobNames = new List<string>();
            await foreach (var blob in Fixture.BlobContainer.GetBlobsAsync(prefix: rootPath))
            {
                blobNames.Add(blob.Name);
            }

            foreach (var blobName in blobNames.OrderByDescending(n => n.Count(c => c == '/')))
            {
                await Fixture.BlobContainer.DeleteBlobAsync(blobName);
            }
        }

        // Exact-id parameterized deletes only (EF interpolation parameterizes {snapshotId}).
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM dbo.snapshot_index WHERE snapshot_id = {snapshotId}");
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM dbo.snapshot_tracking WHERE snapshot_id = {snapshotId}");
    }
}
