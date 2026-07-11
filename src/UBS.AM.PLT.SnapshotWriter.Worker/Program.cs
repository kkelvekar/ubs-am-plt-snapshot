using UBS.AM.PLT.SnapshotWriter.Application;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Blob;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Kafka;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<KafkaConsumerOptions>(
    builder.Configuration.GetSection(KafkaConsumerOptions.SectionName));
builder.Services.Configure<BlobStorageOptions>(
    builder.Configuration.GetSection(BlobStorageOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISnapshotBlobStore, AzureBlobSnapshotStore>();
builder.Services.AddSingleton<ISnapshotMessageHandler, SnapshotMessageHandler>();
builder.Services.AddSingleton<IKafkaConsumerFactory, KafkaConsumerFactory>();
builder.Services.AddHostedService<KafkaSnapshotConsumer>();

var host = builder.Build();
host.Run();
