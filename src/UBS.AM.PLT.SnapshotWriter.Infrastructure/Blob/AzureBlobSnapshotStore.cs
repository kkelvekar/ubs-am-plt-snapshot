using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using UBS.AM.PLT.SnapshotWriter.Application;
using UBS.AM.PLT.SnapshotWriter.Domain;

namespace UBS.AM.PLT.SnapshotWriter.Infrastructure.Blob;

/// <summary>
/// Blob adapter for <see cref="ISnapshotBlobStore"/> using <c>Azure.Storage.Blobs</c>
/// so the same code runs against Azurite locally and ADLS Gen2 in production. The
/// payload is written opaquely via <c>GetRawText()</c>; overwrite makes the write
/// content-idempotent under redelivery. No retry here — the consumer owns retry via
/// redelivery.
/// </summary>
public sealed class AzureBlobSnapshotStore : ISnapshotBlobStore
{
    private readonly BlobContainerClient _container;
    private readonly SemaphoreSlim _containerEnsureLock = new(1, 1);
    private volatile bool _containerEnsured;

    public AzureBlobSnapshotStore(IOptions<BlobStorageOptions> options)
    {
        _container = new BlobContainerClient(options.Value.ConnectionString, options.Value.ContainerName);
    }

    public async Task<string> WriteAsync(SnapshotMessage message, CancellationToken cancellationToken)
    {
        await EnsureContainerExistsAsync(cancellationToken);

        var blob = _container.GetBlobClient(SnapshotBlobPath.FullPath(message));
        var content = new BinaryData(Encoding.UTF8.GetBytes(message.Payload.GetRawText()));
        var uploadOptions = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
        };

        // BlobUploadOptions without access conditions overwrites an existing blob.
        await blob.UploadAsync(content, uploadOptions, cancellationToken);

        return SnapshotBlobPath.RootFolder(message);
    }

    private async Task EnsureContainerExistsAsync(CancellationToken cancellationToken)
    {
        if (_containerEnsured)
        {
            return;
        }

        await _containerEnsureLock.WaitAsync(cancellationToken);
        try
        {
            if (!_containerEnsured)
            {
                await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
                _containerEnsured = true;
            }
        }
        finally
        {
            _containerEnsureLock.Release();
        }
    }
}
