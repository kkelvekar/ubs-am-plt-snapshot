using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Adls;

public static class DependencyInjection
{
    public static IServiceCollection AddAdlsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Fail-fast at host start: a misconfigured pod must crash-loop immediately with a
        // clear reason (the acceptable-crash case) rather than sit Running and fail per
        // message. Validation messages name the missing configuration key exactly.
        services.AddOptions<BlobStorageOptions>()
            .Bind(configuration.GetSection(BlobStorageOptions.SectionName))
            .Validate(
                // Mirror BlobContainerClientFactory: either credential auth (ServiceUri)
                // or the Azurite/local ConnectionString fallback must be present.
                o => !string.IsNullOrWhiteSpace(o.ServiceUri) || !string.IsNullOrWhiteSpace(o.ConnectionString),
                "BlobStorage:ServiceUri or BlobStorage:ConnectionString must be configured (non-empty).")
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.ContainerName),
                "BlobStorage:ContainerName must be configured (non-empty).")
            .ValidateOnStart();

        services.AddSingleton<ISnapshotBlobStore, AzureBlobSnapshotStore>();

        return services;
    }
}
