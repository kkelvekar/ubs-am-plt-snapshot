using Microsoft.Extensions.DependencyInjection;
using UBS.AM.PLT.SnapshotWriter.Application.Contracts;

namespace UBS.AM.PLT.SnapshotWriter.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<ISnapshotMessageHandler, SnapshotMessageHandler>();
        return services;
    }
}
