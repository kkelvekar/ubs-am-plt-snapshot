using Microsoft.Extensions.DependencyInjection;
using UBS.AM.PLT.Snapshot.Application.Contracts;

namespace UBS.AM.PLT.Snapshot.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<ISnapshotMessageHandler, SnapshotMessageHandler>();
        return services;
    }
}
