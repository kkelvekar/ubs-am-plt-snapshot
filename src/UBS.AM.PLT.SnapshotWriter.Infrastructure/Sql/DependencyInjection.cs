using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UBS.AM.PLT.SnapshotWriter.Application.Contracts.Infrastructure;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Sql.Repositories;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Sql.SnapshotConfig;

namespace UBS.AM.PLT.SnapshotWriter.Infrastructure.Sql;

public static class DependencyInjection
{
    public static IServiceCollection AddSqlInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Fail-fast at host start: a misconfigured pod must crash-loop immediately with a
        // clear reason (the acceptable-crash case) rather than sit Running and fail per
        // message. Validation messages name the missing configuration key exactly.
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.ConnectionString),
                "Database:ConnectionString must be configured (non-empty).")
            .ValidateOnStart();

        // The repositories are stateless singletons that take the pooled factory and scope one
        // short-lived DbContext to each call. Pooled contexts reset their state on return to the
        // pool — safe because nothing stashes state on the context between calls.
        // EnableRetryOnFailure / an EF execution strategy is deliberately NOT enabled: retry is
        // owned by Kafka redelivery per design §8/§9 — do not add one.
        services.AddPooledDbContextFactory<SnapshotWriterDbContext>((serviceProvider, options) =>
            options.UseSqlServer(serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));

        services.AddSingleton<ISnapshotTrackingRepository, SnapshotTrackingRepository>();
        services.AddSingleton<ISnapshotIndexRepository, SnapshotIndexRepository>();

        services.AddSingleton<ISnapshotTrackingStore, SqlSnapshotTrackingStore>();
        services.AddSingleton<ISnapshotIndexStore, SqlSnapshotIndexStore>();

        return services;
    }

    public static IServiceCollection AddSnapshotConfigInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // SnapshotConfig is deliberately NOT validated at startup: a missing snapshot type
        // already throws a clear KeyNotFoundException per message, and requiring entries at
        // startup would fight the config-driven-extensibility invariant.
        services.Configure<Dictionary<string, SnapshotTypeConfig>>(
            configuration.GetSection(SnapshotConfigOptions.SectionName));

        services.AddSingleton<IRequiredFilesProvider, SnapshotConfigRequiredFilesProvider>();

        return services;
    }
}
