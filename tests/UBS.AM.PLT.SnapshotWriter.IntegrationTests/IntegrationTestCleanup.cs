using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Files.DataLake;
using Microsoft.EntityFrameworkCore;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Adls;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Sql;

namespace UBS.AM.PLT.SnapshotWriter.IntegrationTests;

internal sealed class IntegrationTestCleanup
{
    private const string SnapshotIdPrefix = "it-";
    private const string RootPrefix = "portfolio_snapshots";

    private readonly IDbContextFactory<SnapshotWriterDbContext> _dbContextFactory;
    private readonly BlobContainerClient _blobContainer;
    private readonly DataLakeFileSystemClient? _fileSystem;
    private readonly IReadOnlyList<string> _testAccountIds;

    public IntegrationTestCleanup(
        IDbContextFactory<SnapshotWriterDbContext> dbContextFactory,
        BlobContainerClient blobContainer,
        BlobStorageOptions blobOptions,
        IntegrationTestSettings settings)
    {
        _dbContextFactory = dbContextFactory;
        _blobContainer = blobContainer;
        _fileSystem = CreateDataLakeFileSystemClient(blobOptions);
        _testAccountIds = settings.TestAccountIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public async Task CleanupSnapshotAsync(string snapshotId)
    {
        if (!snapshotId.StartsWith(SnapshotIdPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Refusing to clean snapshotId '{snapshotId}': integration-test ids must start with '{SnapshotIdPrefix}'.",
                nameof(snapshotId));
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var rootPath = await context.SnapshotTracking
            .AsNoTracking()
            .Where(e => e.SnapshotId == snapshotId)
            .Select(e => e.AdlsRootPath)
            .SingleOrDefaultAsync();

        if (rootPath is not null)
        {
            await DeleteSnapshotFolderAsync(rootPath);
            await DeleteEmptyParentFoldersAsync(rootPath);
        }

        await context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM dbo.SnapshotIndex WHERE SnapshotId = {snapshotId}");
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM dbo.SnapshotTracking WHERE SnapshotId = {snapshotId}");
    }

    public async Task CleanupAllKnownTestDataAsync()
    {
        foreach (var accountId in _testAccountIds)
        {
            await DeleteAccountFoldersAsync(accountId);
            await DeleteSqlRowsForAccountAsync(accountId);
        }
    }

    private async Task DeleteAccountFoldersAsync(string accountId)
    {
        if (_fileSystem is null)
        {
            await DeleteAccountBlobsFallbackAsync(accountId);
            return;
        }

        var accountDirectories = new List<string>();
        try
        {
            await foreach (var path in _fileSystem.GetPathsAsync(RootPrefix, recursive: true))
            {
                if (path.IsDirectory == true
                    && path.Name.EndsWith($"/accountId={accountId}", StringComparison.Ordinal))
                {
                    accountDirectories.Add(path.Name);
                }
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Root path not created yet — nothing to clean up.
            return;
        }

        foreach (var accountDirectory in accountDirectories.OrderByDescending(p => p.Count(c => c == '/')))
        {
            await DeleteDirectoryIfExistsAsync(accountDirectory, recursive: true);
            await DeleteEmptyParentFoldersAsync(accountDirectory);
        }
    }

    private async Task DeleteSnapshotFolderAsync(string rootPath)
    {
        if (_fileSystem is not null)
        {
            await DeleteDirectoryIfExistsAsync(rootPath, recursive: true);
            return;
        }

        var blobNames = await ListBlobNamesIfContainerExistsAsync(rootPath);

        foreach (var blobName in blobNames.OrderByDescending(n => n.Count(c => c == '/')))
        {
            await _blobContainer.DeleteBlobIfExistsAsync(blobName);
        }
    }

    private async Task DeleteEmptyParentFoldersAsync(string path)
    {
        if (_fileSystem is null)
        {
            return;
        }

        var parent = ParentPath(path);
        while (parent is not null)
        {
            await DeleteDirectoryIfExistsAsync(parent, recursive: false);
            parent = ParentPath(parent);
        }
    }

    private async Task DeleteDirectoryIfExistsAsync(string path, bool recursive)
    {
        if (_fileSystem is null)
        {
            return;
        }

        try
        {
            await _fileSystem.GetDirectoryClient(path).DeleteIfExistsAsync(recursive: recursive);
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 409)
        {
            // 404 = already gone; 409 = parent still has non-test children, so leave it.
        }
    }

    private async Task DeleteAccountBlobsFallbackAsync(string accountId)
    {
        var blobNames = (await ListBlobNamesIfContainerExistsAsync(RootPrefix))
            .Where(name => name.Contains($"/accountId={accountId}/", StringComparison.Ordinal));

        foreach (var blobName in blobNames.OrderByDescending(n => n.Count(c => c == '/')))
        {
            await _blobContainer.DeleteBlobIfExistsAsync(blobName);
        }
    }

    /// <summary>
    /// The worker creates the blob container itself on first write (see azurite-local.ps1);
    /// against a freshly started local Azurite it may not exist yet when cleanup runs before
    /// any test has written anything, so a missing container is treated as "nothing to clean"
    /// rather than an error.
    /// </summary>
    private async Task<List<string>> ListBlobNamesIfContainerExistsAsync(string prefix)
    {
        var blobNames = new List<string>();
        try
        {
            await foreach (var blob in _blobContainer.GetBlobsAsync(prefix: prefix))
            {
                blobNames.Add(blob.Name);
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Container not created yet — nothing to clean up.
        }

        return blobNames;
    }

    private async Task DeleteSqlRowsForAccountAsync(string accountId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        await context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM dbo.SnapshotIndex WHERE SnapshotId LIKE 'it-%' AND AccountId = {accountId}");
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM dbo.SnapshotTracking WHERE SnapshotId LIKE 'it-%' AND AccountId = {accountId}");
    }

    /// <summary>
    /// DataLake (DFS) cleanup is only wired up against real ADLS Gen2 (ServiceUri set —
    /// same ServiceUri-wins-over-ConnectionString rule as <see cref="BlobContainerClientFactory"/>).
    /// Azurite's blob emulator does not implement the DFS "filesystem" resource (hierarchical
    /// namespace) — GetPathsAsync returns HTTP 400 against it — so the ConnectionString/local
    /// branch intentionally returns null here and cleanup falls back to the flat-blob-listing
    /// paths already implemented below (DeleteAccountBlobsFallbackAsync /
    /// DeleteSnapshotFolderAsync's non-DataLake branch), which work against both Azurite and
    /// real blob storage.
    /// </summary>
    private static DataLakeFileSystemClient? CreateDataLakeFileSystemClient(BlobStorageOptions options)
    {
        if (!string.IsNullOrEmpty(options.ServiceUri))
        {
            var fileSystemUri = ToDfsFileSystemUri(options.ServiceUri, options.ContainerName);
            return new DataLakeFileSystemClient(fileSystemUri, new DefaultAzureCredential());
        }

        return null;
    }

    private static Uri ToDfsFileSystemUri(string blobServiceUri, string fileSystemName)
    {
        var builder = new UriBuilder($"{blobServiceUri.TrimEnd('/')}/{fileSystemName}");
        builder.Host = builder.Host.Replace(".blob.", ".dfs.", StringComparison.OrdinalIgnoreCase);
        return builder.Uri;
    }

    private static string? ParentPath(string path)
    {
        var index = path.LastIndexOf('/');
        return index > 0 ? path[..index] : null;
    }
}
