using UBS.AM.PLT.SnapshotWriter.Application;
using UBS.AM.PLT.SnapshotWriter.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

var host = builder.Build();
host.Run();
