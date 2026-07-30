using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka;

public sealed class KafkaConsumerFactory : IKafkaConsumerFactory
{
    private readonly KafkaConsumerOptions _options;
    private readonly ILogger<KafkaConsumerFactory> _logger;

    public KafkaConsumerFactory(
        IOptions<KafkaConsumerOptions> options,
        ILogger<KafkaConsumerFactory> logger)
    {
        _options = options.Value;
        _logger = logger;
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

        return new ConsumerBuilder<string, string>(config)
            // Surface librdkafka's internal error/log signal into application logging;
            // without these handlers broker-unreachable / auth failures only reach stderr
            // via librdkafka defaults ("nothing consuming, zero log signal" in prod).
            .SetErrorHandler((_, error) => KafkaClientDiagnostics.HandleError(_logger, error))
            .SetLogHandler((_, logMessage) => KafkaClientDiagnostics.HandleLog(_logger, logMessage))
            .Build();
    }
}
