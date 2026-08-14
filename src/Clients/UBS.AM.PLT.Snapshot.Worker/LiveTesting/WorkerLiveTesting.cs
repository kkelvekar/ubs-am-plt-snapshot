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
    }

    public static void MapEndpoints(WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapControllers();
        }
    }
}
