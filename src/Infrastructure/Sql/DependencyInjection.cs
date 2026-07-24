using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql.Repositories;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql.SnapshotConfig;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Sql;

public static class DependencyInjection
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

    /// <summary>
    /// Read-side registration for the Load-snapshots grid Read API (solution design section 7).
    /// Wires only the shared options + pooled DbContext factory and the read-only
    /// ISnapshotIndexQuery (implemented by the same SnapshotIndexRepository the write side
    /// uses for the UPSERT) - deliberately NONE of the write-path stores or the
    /// required-files provider, which the Read API never touches. The grid filter resolver
    /// is a pure static rule (SnapshotGridFilter.Resolve), so it needs no registration
    /// here; the composition root supplies the TimeProvider.
    /// </summary>
    public static IServiceCollection AddSqlReadInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddSnapshotDbContextFactory(connectionString);

        services.AddSingleton<ISnapshotIndexQuery, SnapshotIndexRepository>();

        return services;
    }

    private static IServiceCollection AddSnapshotDbContextFactory(this IServiceCollection services, string connectionString)
    {
        // Fail-fast at host start: a misconfigured pod must crash-loop immediately with a
        // clear reason (the acceptable-crash case) rather than sit Running and fail per
        // message. Validation messages name the missing configuration key exactly.
        services.AddOptions<DatabaseOptions>()
            .Configure(o => o.ConnectionString = connectionString)
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.ConnectionString),
                "Database:ConnectionString must be configured (non-empty).")
            .ValidateOnStart();

        // The repositories are stateless singletons that take the pooled factory and scope
        // one short-lived DbContext to each call. Pooled contexts reset their state on
        // return to the pool - safe because nothing stashes state on the context between calls.
        // EnableRetryOnFailure / an EF execution strategy is deliberately NOT enabled: retry is
        // owned by Kafka redelivery per design section 8/9 - do not add one.
        services.AddPooledDbContextFactory<SnapshotDbContext>((serviceProvider, options) =>
            options.UseSqlServer(serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));

        return services;
    }

    public static IServiceCollection AddSnapshotConfigInfrastructure(this IServiceCollection services)
    {
        // The required-files map is deliberately NOT validated at startup: a missing snapshot
        // type already throws a clear KeyNotFoundException per message, so an entry absent from
        // the library-owned SnapshotConfigDefinition surfaces with a clear reason at the point
        // it actually matters rather than blocking host start.
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
