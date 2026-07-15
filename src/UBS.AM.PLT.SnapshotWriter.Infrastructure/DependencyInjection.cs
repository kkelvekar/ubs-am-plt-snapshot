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
        services.Configure<KafkaConsumerOptions>(
            configuration.GetSection(KafkaConsumerOptions.SectionName));
        services.Configure<BlobStorageOptions>(
            configuration.GetSection(BlobStorageOptions.SectionName));
        services.Configure<DatabaseOptions>(
            configuration.GetSection(DatabaseOptions.SectionName));
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
