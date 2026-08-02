using Microsoft.Extensions.DependencyInjection;
using UBS.AM.PLT.Snapshot.Application.Contracts;
using UBS.AM.PLT.Snapshot.Application.Contracts.Application;
using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotGrid;
using UBS.AM.PLT.Snapshot.Application.Features.SnapshotIngestion;

namespace UBS.AM.PLT.Snapshot.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<ISnapshotMessageHandler, SnapshotMessageHandler>();
        return services;
    }

    /// <summary>
    /// Registers only the Load-snapshots grid feature (solution design section 7). The Api
    /// client calls this instead of AddApplication, so the read process never registers the
    /// write-side SnapshotMessageHandler or its dependencies.
    /// </summary>
    public static IServiceCollection AddPortfolioSnapshotGrid(this IServiceCollection services)
    {
        services.AddSingleton<IPortfolioSnapshotGridQueryHandler, PortfolioSnapshotGridQueryHandler>();
        return services;
    }
}
