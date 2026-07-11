using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UBS.AM.PLT.SnapshotWriter.Application;
using UBS.AM.PLT.SnapshotWriter.Application.Interfaces;
using UBS.AM.PLT.SnapshotWriter.Application.Interfaces.Infrastructure;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Blob;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Kafka;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<KafkaConsumerOptions>(
    builder.Configuration.GetSection(KafkaConsumerOptions.SectionName));
builder.Services.Configure<BlobStorageOptions>(
    builder.Configuration.GetSection(BlobStorageOptions.SectionName));
builder.Services.Configure<DatabaseOptions>(
    builder.Configuration.GetSection(DatabaseOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);

// The tracking store is a singleton like the rest of the pipeline, so it takes the
// factory and creates a short-lived DbContext per operation.
builder.Services.AddDbContextFactory<SnapshotWriterDbContext>((serviceProvider, options) =>
    options.UseSqlServer(serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));

builder.Services.AddSingleton<ISnapshotBlobStore, AzureBlobSnapshotStore>();
builder.Services.AddSingleton<ISnapshotTrackingStore, SqlSnapshotTrackingStore>();
builder.Services.AddSingleton<ISnapshotMessageHandler, SnapshotMessageHandler>();
builder.Services.AddSingleton<IKafkaConsumerFactory, KafkaConsumerFactory>();
builder.Services.AddHostedService<KafkaSnapshotConsumer>();

var host = builder.Build();
host.Run();
