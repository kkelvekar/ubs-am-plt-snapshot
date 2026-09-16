using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Application.Features.LiveTestCleanup;
using UBS.AM.PLT.Snapshot.Infrastructure.Adls;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql;

namespace UBS.AM.PLT.Snapshot.Worker.LiveTesting;

internal static class WorkerLiveTesting
{
    public static void AddServices(WebApplicationBuilder builder)
    {
        if (!builder.Environment.IsDevelopment())
        {
            return;
        }

        builder.Services.AddControllers();
        builder.Services.AddSingleton<SnapshotTemplateLoader>();
        builder.Services.AddSingleton<ILiveTestSnapshotCleanupStore, SqlLiveTestSnapshotCleanupStore>();
        builder.Services.AddSingleton<ILiveTestSnapshotBlobCleanup, AzureBlobLiveTestSnapshotCleanup>();
        builder.Services.AddSingleton<LiveTestSnapshotCleanup>();
    }

    public static void MapEndpoints(WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapControllers();
        }
    }
}
