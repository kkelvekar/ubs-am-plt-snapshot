using System.Text.Json;
using Confluent.Kafka;

namespace UBS.AM.PLT.Snapshot.Worker.LiveTesting;

public interface ISnapshotSimulationPublisher
{
    Task<SnapshotSimulationResult> PublishAsync(
        SnapshotSimulationRequest request,
        CancellationToken cancellationToken);
}

internal sealed class KafkaSnapshotSimulationPublisher : ISnapshotSimulationPublisher, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly SnapshotTemplateLoader templateLoader;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<KafkaSnapshotSimulationPublisher> logger;
    private readonly IProducer<string, string> producer;
    private readonly string topic;

    public KafkaSnapshotSimulationPublisher(
        IConfiguration configuration,
        SnapshotTemplateLoader templateLoader,
        TimeProvider timeProvider,
        ILogger<KafkaSnapshotSimulationPublisher> logger)
    {
        this.templateLoader = templateLoader;
        this.timeProvider = timeProvider;
        this.logger = logger;

        var bootstrapServers = configuration["Kafka:BootstrapServers"];
        topic = configuration["Kafka:Topics:snapshot-request"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(bootstrapServers) || string.IsNullOrWhiteSpace(topic))
        {
            throw new InvalidOperationException(
                "Kafka:BootstrapServers and Kafka:Topics:snapshot-request are required for live testing.");
        }

        producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            Acks = Acks.All,
        }).Build();
    }

    public async Task<SnapshotSimulationResult> PublishAsync(
        SnapshotSimulationRequest request,
        CancellationToken cancellationToken)
    {
        var template = templateLoader.Load(request.TemplateFileName);
        var messages = SnapshotGenerator.Generate(template, request, timeProvider.GetUtcNow());
        var deliveries = new List<SnapshotSimulationDelivery>(messages.Count);

        foreach (var (message, index) in messages.Select((message, index) => (message, index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = JsonSerializer.Serialize(message.Request, JsonOptions);
            var result = await producer.ProduceAsync(
                topic,
                new Message<string, string> { Key = message.AccountId, Value = json },
                cancellationToken);

            deliveries.Add(new SnapshotSimulationDelivery(
                message.Request.SnapshotId,
                message.Request.AccountId,
                message.Request.PayloadType,
                result.Topic,
                result.Partition.Value,
                result.Offset.Value));

            logger.LogInformation(
                "Published live-test payload; snapshotId={SnapshotId}, accountId={AccountId}, payloadType={PayloadType}, topic={Topic}, partition={Partition}, offset={Offset}",
                message.Request.SnapshotId,
                message.Request.AccountId,
                message.Request.PayloadType,
                result.Topic,
                result.Partition.Value,
                result.Offset.Value);

            if (index == messages.Count - 1)
            {
                continue;
            }

            var nextMessage = messages[index + 1];
            var delay = nextMessage.Request.SnapshotId == message.Request.SnapshotId
                ? request.MessageDelay
                : NextSnapshotDelay(request);

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, timeProvider, cancellationToken);
            }
        }

        producer.Flush(TimeSpan.FromSeconds(10));
        return new SnapshotSimulationResult(
            messages.Select(message => message.Request.SnapshotId).Distinct().ToArray(),
            messages.Count,
            deliveries);
    }

    public void Dispose()
    {
        producer.Flush(TimeSpan.FromSeconds(10));
        producer.Dispose();
    }

    private static TimeSpan NextSnapshotDelay(SnapshotSimulationRequest request)
    {
        if (request.SnapshotDelayMin == request.SnapshotDelayMax)
        {
            return request.SnapshotDelayMin;
        }

        return TimeSpan.FromTicks(Random.Shared.NextInt64(
            request.SnapshotDelayMin.Ticks,
            request.SnapshotDelayMax.Ticks + 1));
    }
}
