using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka;

public sealed class KafkaProducerFactory : IKafkaProducerFactory
{
    private readonly KafkaProducerOptions _options;
    private readonly ILogger<KafkaProducerFactory> _logger;

    public KafkaProducerFactory(
        IOptions<KafkaProducerOptions> options,
        ILogger<KafkaProducerFactory> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public IProducer<string, string> Create()
    {
        var config = new ProducerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            // Wait for all in-sync replicas: the response is the publisher's only signal
            // that its snapshot landed, so a broker acknowledging before replication would
            // let a leader failover lose it silently.
            Acks = Acks.All,
            EnableIdempotence = true,
        };

        return new ProducerBuilder<string, string>(config)
            .SetErrorHandler((_, error) => KafkaClientDiagnostics.HandleError(_logger, error))
            .SetLogHandler((_, logMessage) => KafkaClientDiagnostics.HandleLog(_logger, logMessage))
            .Build();
    }
}
