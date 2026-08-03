using Microsoft.Extensions.DependencyInjection;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Adls;

public static class AdlsRegistration
{
    public static IServiceCollection AddAdlsInfrastructure(this IServiceCollection services, string serviceUri, string containerName, string? connectionString = null)
    {
        services.AddBlobStorageOptions(serviceUri, containerName, connectionString);

        services.AddSingleton<ISnapshotBlobStore, AzureBlobSnapshotStore>();

        return services;
    }

    // Read-side only: registers ISnapshotPayloadQuery, not the write-flow ISnapshotBlobStore,
    // so the read process can never resolve a container-creating or payload-overwriting port.
    public static IServiceCollection AddAdlsReadInfrastructure(this IServiceCollection services, string serviceUri, string containerName, string? connectionString = null)
    {
        services.AddBlobStorageOptions(serviceUri, containerName, connectionString);

        services.AddSingleton<ISnapshotPayloadQuery, AzureBlobSnapshotStore>();

        return services;
    }

    private static IServiceCollection AddBlobStorageOptions(this IServiceCollection services, string serviceUri, string containerName, string? connectionString)
    {
        // Fail-fast at host start rather than per message.
        services.AddOptions<BlobStorageOptions>()
            .Configure(o =>
            {
                o.ServiceUri = serviceUri;
                o.ContainerName = containerName;
                o.ConnectionString = connectionString ?? string.Empty;
            })
            .Validate(
                // Mirrors BlobContainerClientFactory: ServiceUri (credential auth) or
                // ConnectionString (Azurite/local) must be present.
                o => !string.IsNullOrWhiteSpace(o.ServiceUri) || !string.IsNullOrWhiteSpace(o.ConnectionString),
                "BlobStorage:ServiceUri or BlobStorage:ConnectionString must be configured (non-empty).")
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.ContainerName),
                "BlobStorage:ContainerName must be configured (non-empty).")
            .ValidateOnStart();

        return services;
    }
}
