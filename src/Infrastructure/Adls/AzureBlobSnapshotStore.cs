using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotDetail;
using UBS.AM.PLT.Snapshot.Domain;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Adls;

/// <summary>
/// Blob adapter for <see cref="ISnapshotBlobStore"/> (write flow) and
/// <see cref="ISnapshotPayloadQuery"/> (snapshot-detail read, solution design section 10)
/// using <c>Azure.Storage.Blobs</c> so the same code runs against Azurite locally
/// (connection string) and ADLS Gen2 (credential auth via
/// <see cref="BlobContainerClientFactory"/>) with no change. One class owns all blob access
/// rather than splitting read and write into separate adapters — the same precedent as
/// PortfolioSnapshotIndexRepository on the SQL side; the two ports stay separate so the Read API
/// composition root can register the read one alone. The payload string is written opaquely
/// and verbatim — never re-serialised from anything parsed, so every delivery produces
/// byte-identical content and overwrite makes the write content-idempotent under redelivery,
/// and the read path hands back those exact stored bytes. No retry here — the consumer owns
/// retry via redelivery.
/// </summary>
public sealed class AzureBlobSnapshotStore : ISnapshotBlobStore, ISnapshotPayloadQuery
{
    /// <summary>Blob filename suffix, matching <see cref="SnapshotBlobPath.FileName"/>.</summary>
    private const string JsonSuffix = ".json";

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

    public async Task<string?> ReadPayloadAsync(string adlsRootPath, string payloadType, CancellationToken cancellationToken)
    {
        // No EnsureContainerExistsAsync on the read path: a read must never create anything.
        var blob = _container.GetBlobClient(SnapshotBlobPath.FullPath(adlsRootPath, payloadType));

        try
        {
            var download = await blob.DownloadContentAsync(cancellationToken);

            return download.Value.Content.ToString();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Missing blob (or missing container) is an absent payload, not a fault - the
            // caller turns null into 404.
            return null;
        }
    }

    public async Task<IReadOnlyList<SnapshotPayloadFile>> ReadAllPayloadsAsync(string adlsRootPath, CancellationToken cancellationToken)
    {
        var prefix = $"{adlsRootPath}/";
        var payloads = new List<SnapshotPayloadFile>();

        try
        {
            await foreach (var item in _container.GetBlobsAsync(prefix: prefix, cancellationToken: cancellationToken))
            {
                var relativeName = item.Name[prefix.Length..];

                // The write path never nests below the snapshot root, so anything deeper (or
                // not a .json file) is not a payload of this snapshot and is skipped.
                if (relativeName.Contains('/', StringComparison.Ordinal)
                    || !relativeName.EndsWith(JsonSuffix, StringComparison.Ordinal))
                {
                    continue;
                }

                // Exact inverse of SnapshotBlobPath.FileName.
                var payloadType = relativeName[..^JsonSuffix.Length];

                var blob = _container.GetBlobClient(item.Name);
                var download = await blob.DownloadContentAsync(cancellationToken);

                payloads.Add(new SnapshotPayloadFile
                {
                    PayloadType = payloadType,
                    Json = download.Value.Content.ToString(),
                });
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Container absent - same meaning as an empty root folder.
            return [];
        }

        return payloads;
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
