using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Application.Features.LiveTestCleanup;
using UBS.AM.PLT.Snapshot.Domain;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Adls;

public sealed class AzureBlobLiveTestSnapshotCleanup : ILiveTestSnapshotBlobCleanup
{
    private readonly BlobContainerClient container;

    public AzureBlobLiveTestSnapshotCleanup(BlobContainerClient container)
    {
        this.container = container;
    }

    public AzureBlobLiveTestSnapshotCleanup(IOptions<BlobStorageOptions> options)
    {
        var clientOptions = new BlobClientOptions();
        clientOptions.Retry.MaxRetries = 0;
        container = BlobContainerClientFactory.Create(options.Value, clientOptions);
    }

    public async Task<IReadOnlyList<LiveTestSnapshotLocation>> ListAsync(CancellationToken cancellationToken)
    {
        var locations = new HashSet<LiveTestSnapshotLocation>();
        try
        {
            await foreach (var blob in container.GetBlobsAsync(prefix: LiveTestSnapshot.StoragePrefix, cancellationToken: cancellationToken))
            {
                if (LiveTestSnapshot.TryGetLocation(blob.Name, out var directorySnapshotId, out var directoryAccountId))
                {
                    locations.Add(new LiveTestSnapshotLocation(directorySnapshotId, directoryAccountId, blob.Name));
                    continue;
                }

                var separator = blob.Name.LastIndexOf('/');
                if (separator > 0
                    && LiveTestSnapshot.TryGetLocation(blob.Name[..separator], out var snapshotId, out var accountId))
                {
                    locations.Add(new LiveTestSnapshotLocation(snapshotId, accountId, blob.Name[..separator]));
                }
            }
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == "ContainerNotFound")
        {
            return [];
        }

        return locations.ToArray();
    }

    public async Task<int> DeleteAsync(LiveTestSnapshotLocation location, CancellationToken cancellationToken)
    {
        LiveTestSnapshotCleanup.Validate(location);
        if (location.RootPath.Length == 0)
        {
            return 0;
        }

        var deleted = 0;
        try
        {
            // The slash prevents prefix collisions with neighbouring snapshot folders.
            await foreach (var blob in container.GetBlobsAsync(prefix: $"{location.RootPath}/", cancellationToken: cancellationToken))
            {
                if (blob.Name[(location.RootPath.Length + 1)..].Contains('/', StringComparison.Ordinal))
                {
                    continue;
                }

                var result = await container.DeleteBlobIfExistsAsync(blob.Name,
                    DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);
                if (result.Value)
                {
                    deleted++;
                }
            }

            // Delete only the empty ADLS snapshot directory, never shared parents.
            await container.DeleteBlobIfExistsAsync(location.RootPath, cancellationToken: cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == "ContainerNotFound")
        {
            return deleted;
        }

        return deleted;
    }
}
