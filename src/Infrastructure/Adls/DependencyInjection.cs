using Microsoft.Extensions.DependencyInjection;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Adls;

public static class DependencyInjection
{
    public static IServiceCollection AddAdlsInfrastructure(this IServiceCollection services, string serviceUri, string containerName, string? connectionString = null)
    {
        services.AddBlobStorageOptions(serviceUri, containerName, connectionString);

        services.AddSingleton<ISnapshotBlobStore, AzureBlobSnapshotStore>();

        return services;
    }

    /// <summary>
    /// Read-side registration for the snapshot-detail Read API (solution design section 10,
    /// Screen 2). Wires the same blob options plus ONLY the read-only ISnapshotPayloadQuery
    /// (implemented by the same AzureBlobSnapshotStore the write side uses) - deliberately
    /// NOT the write-flow ISnapshotBlobStore, so the read process can never resolve a
    /// container-creating or payload-overwriting port.
    /// </summary>
    public static IServiceCollection AddAdlsReadInfrastructure(this IServiceCollection services, string serviceUri, string containerName, string? connectionString = null)
    {
        services.AddBlobStorageOptions(serviceUri, containerName, connectionString);

        services.AddSingleton<ISnapshotPayloadQuery, AzureBlobSnapshotStore>();

        return services;
    }

    private static IServiceCollection AddBlobStorageOptions(this IServiceCollection services, string serviceUri, string containerName, string? connectionString)
    {
        // Fail-fast at host start: a misconfigured pod must crash-loop immediately with a
        // clear reason (the acceptable-crash case) rather than sit Running and fail per
        // message. Validation messages name the missing configuration key exactly.
        services.AddOptions<BlobStorageOptions>()
            .Configure(o =>
            {
                o.ServiceUri = serviceUri;
                o.ContainerName = containerName;
                o.ConnectionString = connectionString ?? string.Empty;
            })
            .Validate(
                // Mirror BlobContainerClientFactory: either credential auth (ServiceUri)
                // or the Azurite/local ConnectionString fallback must be present.
                o => !string.IsNullOrWhiteSpace(o.ServiceUri) || !string.IsNullOrWhiteSpace(o.ConnectionString),
                "BlobStorage:ServiceUri or BlobStorage:ConnectionString must be configured (non-empty).")
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.ContainerName),
                "BlobStorage:ContainerName must be configured (non-empty).")
            .ValidateOnStart();

        return services;
    }
}
