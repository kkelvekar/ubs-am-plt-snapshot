using UBS.AM.PLT.SnapshotWriter.Application;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Kafka;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<KafkaConsumerOptions>(
    builder.Configuration.GetSection(KafkaConsumerOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISnapshotMessageHandler, SnapshotMessageHandler>();
builder.Services.AddSingleton<IKafkaConsumerFactory, KafkaConsumerFactory>();
builder.Services.AddHostedService<KafkaSnapshotConsumer>();

var host = builder.Build();
host.Run();
