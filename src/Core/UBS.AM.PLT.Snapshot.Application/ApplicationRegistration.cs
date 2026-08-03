using Microsoft.Extensions.DependencyInjection;
using UBS.AM.PLT.Snapshot.Application.Contracts;
using UBS.AM.PLT.Snapshot.Application.Contracts.Application;
using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotDetail;
using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotGrid;
using UBS.AM.PLT.Snapshot.Application.Features.SnapshotIngestion;

namespace UBS.AM.PLT.Snapshot.Application;

public static class ApplicationRegistration
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<ISnapshotMessageHandler, SnapshotMessageHandler>();
        return services;
    }

    // Read-side only: Api calls this instead of AddApplication so the write-side
    // SnapshotMessageHandler is never registered in the read process.
    public static IServiceCollection AddPortfolioSnapshotReadFeatures(this IServiceCollection services)
    {
        services.AddSingleton<IPortfolioSnapshotGridQueryHandler, PortfolioSnapshotGridQueryHandler>();
        services.AddSingleton<IPortfolioSnapshotDetailQueryHandler, PortfolioSnapshotDetailQueryHandler>();
        return services;
    }
}
