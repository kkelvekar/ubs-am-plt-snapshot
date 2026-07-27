using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Domain;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Adls;

/// <summary>
/// Blob adapter for <see cref="ISnapshotBlobStore"/> using <c>Azure.Storage.Blobs</c>
/// so the same code runs against Azurite locally (connection string) and ADLS Gen2
/// (credential auth via <see cref="BlobContainerClientFactory"/>) with no change. The
/// payload string is written opaquely and verbatim — never re-serialised from anything
/// parsed, so every delivery produces byte-identical content and overwrite makes the
/// write content-idempotent under redelivery. No retry here — the consumer owns retry via
/// redelivery.
/// </summary>
public sealed class AzureBlobSnapshotStore : ISnapshotBlobStore
{
    private readonly BlobContainerClient _container;
    private readonly SemaphoreSlim _containerEnsureLock = new(1, 1);
    private volatile bool _containerEnsured;

    public AzureBlobSnapshotStore(IOptions<BlobStorageOptions> options)
    {
        // MaxRetries = 0 disables the SDK's own internal retry policy so a failure
        // surfaces to the consumer immediately — otherwise the SDK silently absorbs
        // transient failures for well over a minute, distorting the consumer's
        // configured retry/alert cadence.
        var clientOptions = new BlobClientOptions();
        clientOptions.Retry.MaxRetries = 0;

        _container = BlobContainerClientFactory.Create(options.Value, clientOptions);
    }

    public async Task WriteAsync(SnapshotMessage message, string rootPath, CancellationToken cancellationToken)
    {
        await EnsureContainerExistsAsync(cancellationToken);

        var blob = _container.GetBlobClient(SnapshotBlobPath.FullPath(rootPath, message.PayloadType));
        var content = new BinaryData(Encoding.UTF8.GetBytes(message.Payload));
        var uploadOptions = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
        };

        // BlobUploadOptions without access conditions overwrites an existing blob.
        await blob.UploadAsync(content, uploadOptions, cancellationToken);
    }

    public async Task<string> ReadHeaderAsync(string adlsRootPath, CancellationToken cancellationToken)
    {
        var blob = _container.GetBlobClient($"{adlsRootPath}/{SnapshotBlobPath.FileName("header")}");
        var download = await blob.DownloadContentAsync(cancellationToken);

        return download.Value.Content.ToString();
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
