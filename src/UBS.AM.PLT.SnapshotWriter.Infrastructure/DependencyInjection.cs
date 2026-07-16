using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UBS.AM.PLT.SnapshotWriter.Application.Contracts.Infrastructure;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Blob;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Kafka;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence.Repositories;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.SnapshotConfig;

namespace UBS.AM.PLT.SnapshotWriter.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Fail-fast at host start: a misconfigured pod must crash-loop immediately with a
        // clear reason (the acceptable-crash case) rather than sit Running and fail per
        // message. Validation messages name the missing configuration key exactly.
        services.AddOptions<KafkaConsumerOptions>()
            .Bind(configuration.GetSection(KafkaConsumerOptions.SectionName))
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.BootstrapServers),
                "Kafka:BootstrapServers must be configured (non-empty).")
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.Topic),
                "Kafka:Topic must be configured (non-empty).")
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.ConsumerGroup),
                "Kafka:ConsumerGroup must be configured (non-empty).")
            .ValidateOnStart();

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

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.ConnectionString),
                "Database:ConnectionString must be configured (non-empty).")
            .ValidateOnStart();

        // SnapshotConfig is deliberately NOT validated at startup: a missing snapshot type
        // already throws a clear KeyNotFoundException per message, and requiring entries at
        // startup would fight the config-driven-extensibility invariant.
        services.Configure<Dictionary<string, SnapshotTypeConfig>>(
            configuration.GetSection(SnapshotConfigOptions.SectionName));

        // The repositories are stateless singletons that take the pooled factory and scope one
        // short-lived DbContext to each call. Pooled contexts reset their state on return to the
        // pool — safe because nothing stashes state on the context between calls.
        // EnableRetryOnFailure / an EF execution strategy is deliberately NOT enabled: retry is
        // owned by Kafka redelivery per design §8/§9 — do not add one.
        services.AddPooledDbContextFactory<SnapshotWriterDbContext>((serviceProvider, options) =>
            options.UseSqlServer(serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));

        services.AddSingleton<ISnapshotTrackingRepository, SnapshotTrackingRepository>();
        services.AddSingleton<ISnapshotIndexRepository, SnapshotIndexRepository>();

        services.AddSingleton<ISnapshotBlobStore, AzureBlobSnapshotStore>();
        services.AddSingleton<ISnapshotTrackingStore, SqlSnapshotTrackingStore>();
        services.AddSingleton<IRequiredFilesProvider, SnapshotConfigRequiredFilesProvider>();
        services.AddSingleton<ISnapshotIndexStore, SqlSnapshotIndexStore>();
        services.AddSingleton<IKafkaConsumerFactory, KafkaConsumerFactory>();
        services.AddHostedService<KafkaSnapshotConsumer>();

        return services;
    }
}
