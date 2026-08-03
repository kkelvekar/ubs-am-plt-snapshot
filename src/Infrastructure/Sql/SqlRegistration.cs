using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql.Repositories;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql.SnapshotConfig;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Sql;

public static class SqlRegistration
{
    public static IServiceCollection AddSqlInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddSnapshotDbContextFactory(connectionString);

        services.AddSingleton<ISnapshotTrackingRepository, SnapshotTrackingRepository>();
        services.AddSingleton<ISnapshotIndexRepository, SnapshotIndexRepository>();

        services.AddSingleton<ISnapshotTrackingStore, SqlSnapshotTrackingStore>();
        services.AddSingleton<ISnapshotIndexStore, SqlSnapshotIndexStore>();

        return services;
    }

    // Read-side only: registers ISnapshotIndexQuery (same SnapshotIndexRepository the write
    // side uses), not the write-path stores or the required-files provider.
    public static IServiceCollection AddSqlReadInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddSnapshotDbContextFactory(connectionString);

        services.AddSingleton<ISnapshotIndexQuery, SnapshotIndexRepository>();

        return services;
    }

    private static IServiceCollection AddSnapshotDbContextFactory(this IServiceCollection services, string connectionString)
    {
        // Fail-fast at host start rather than per message.
        services.AddOptions<DatabaseOptions>()
            .Configure(o => o.ConnectionString = connectionString)
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.ConnectionString),
                "Database:ConnectionString must be configured (non-empty).")
            .ValidateOnStart();

        // Repositories are stateless singletons taking the pooled factory and scoping one
        // short-lived DbContext per call. EnableRetryOnFailure is deliberately not enabled:
        // retry is owned by Kafka redelivery (design section 8/9).
        services.AddPooledDbContextFactory<SnapshotDbContext>((serviceProvider, options) =>
            options.UseSqlServer(serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));

        return services;
    }

    public static IServiceCollection AddSnapshotConfigInfrastructure(this IServiceCollection services)
    {
        // Not validated at startup: a missing snapshot type already throws a clear
        // KeyNotFoundException per message.
        services.Configure<Dictionary<string, SnapshotTypeConfig>>(map =>
        {
            foreach (var (type, config) in SnapshotConfigDefinition.Map)
            {
                map[type] = config;
            }
        });

        services.AddSingleton<IRequiredFilesProvider, SnapshotConfigRequiredFilesProvider>();

        return services;
    }
}
