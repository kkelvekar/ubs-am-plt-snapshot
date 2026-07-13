using Azure.Identity;
using Azure.Storage.Blobs;

namespace UBS.AM.PLT.SnapshotWriter.Infrastructure.Blob;

/// <summary>
/// The single place the blob authentication selection rule lives: a configured
/// <see cref="BlobStorageOptions.ServiceUri"/> means credential auth via
/// <c>DefaultAzureCredential</c> (az login locally, managed identity in AKS);
/// otherwise <see cref="BlobStorageOptions.ConnectionString"/> is the Azurite/local
/// fallback. ServiceUri wins when both are set.
/// </summary>
internal static class BlobContainerClientFactory
{
    internal static BlobContainerClient Create(BlobStorageOptions options, BlobClientOptions? clientOptions = null)
    {
        if (!string.IsNullOrEmpty(options.ServiceUri))
        {
            return new BlobContainerClient(
                new Uri($"{options.ServiceUri.TrimEnd('/')}/{options.ContainerName}"),
                new DefaultAzureCredential(),
                clientOptions);
        }

        if (!string.IsNullOrEmpty(options.ConnectionString))
        {
            return new BlobContainerClient(options.ConnectionString, options.ContainerName, clientOptions);
        }

        throw new InvalidOperationException("BlobStorage requires either ServiceUri (credential auth) or ConnectionString.");
    }
}
