using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UBS.AM.PLT.SnapshotWriter.Application;
using UBS.AM.PLT.SnapshotWriter.Application.Interfaces;
using UBS.AM.PLT.SnapshotWriter.Application.Interfaces.Infrastructure;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Blob;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Configuration;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Kafka;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<KafkaConsumerOptions>(
    builder.Configuration.GetSection(KafkaConsumerOptions.SectionName));
builder.Services.Configure<BlobStorageOptions>(
    builder.Configuration.GetSection(BlobStorageOptions.SectionName));
builder.Services.Configure<DatabaseOptions>(
    builder.Configuration.GetSection(DatabaseOptions.SectionName));
builder.Services.Configure<Dictionary<string, SnapshotTypeConfig>>(
    builder.Configuration.GetSection(SnapshotConfigOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);

// The repositories are stateless singletons that take the pooled factory and scope one
// short-lived DbContext to each call. Pooled contexts reset their state on return to the
// pool — safe because nothing stashes state on the context between calls.
// EnableRetryOnFailure / an EF execution strategy is deliberately NOT enabled: retry is
// owned by Kafka redelivery per design §8/§9 — do not add one.
builder.Services.AddPooledDbContextFactory<SnapshotWriterDbContext>((serviceProvider, options) =>
    options.UseSqlServer(serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));

builder.Services.AddSingleton<ISnapshotTrackingRepository, SnapshotTrackingRepository>();
builder.Services.AddSingleton<ISnapshotIndexRepository, SnapshotIndexRepository>();

builder.Services.AddSingleton<ISnapshotBlobStore, AzureBlobSnapshotStore>();
builder.Services.AddSingleton<ISnapshotTrackingStore, SqlSnapshotTrackingStore>();
builder.Services.AddSingleton<IRequiredFilesProvider, SnapshotConfigRequiredFilesProvider>();
builder.Services.AddSingleton<ISnapshotIndexStore, SqlSnapshotIndexStore>();
builder.Services.AddSingleton<ISnapshotMessageHandler, SnapshotMessageHandler>();
builder.Services.AddSingleton<IKafkaConsumerFactory, KafkaConsumerFactory>();
builder.Services.AddHostedService<KafkaSnapshotConsumer>();

var host = builder.Build();
host.Run();
