using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace UBS.AM.PLT.SnapshotWriter.Infrastructure.Kafka;

public sealed class KafkaConsumerFactory : IKafkaConsumerFactory
{
    private readonly KafkaConsumerOptions _options;

    public KafkaConsumerFactory(IOptions<KafkaConsumerOptions> options)
    {
        _options = options.Value;
    }

    public IConsumer<string, string> Create()
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.ConsumerGroup,
            // Offsets are committed manually, only after all writes for a message succeed.
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
        };

        return new ConsumerBuilder<string, string>(config).Build();
    }
}
