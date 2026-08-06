using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UBS.Advantage.CommunicationModels.Snapshot;
using UBS.Advantage.Messaging;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Commands;
using UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Configuration;
using UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Consuming;
using UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Publishing;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka;

public static class KafkaRegistration
{
    public static IServiceCollection AddKafkaInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Fail-fast at host start rather than per message.
        services.AddOptions<KafkaConsumerOptions>()
            .Bind(configuration.GetSection(KafkaConsumerOptions.SectionName))
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.BootstrapServers),
                "Kafka:BootstrapServers must be configured (non-empty).")
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.Topic),
                "Kafka:Topic must be configured (non-empty).")
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.ConsumerGroup),
                "Kafka:ConsumerGroup must be configured (non-empty).")
            .ValidateOnStart();

        services.AddOptions<KafkaProducerOptions>()
            .Bind(configuration.GetSection(KafkaProducerOptions.SectionName))
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.BootstrapServers),
                "Kafka:BootstrapServers must be configured (non-empty).")
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.ResponseTopic),
                "Kafka:ResponseTopic must be configured (non-empty).")
            .ValidateOnStart();

        // Commands are registered by their framework contract, not their concrete type: each is
        // resolved for the message type it handles, exactly as the org library will resolve them
        // once the consumer and publisher below are dropped. Both are singletons — the publish
        // command owns the one long-lived producer for the process and is disposed with the host.
        services.AddSingleton<ACommand<IMessage<string, SnapshotRequest>>, SnapshotRequestCommand>();
        services.AddSingleton<ACommand<IMessage<string, SnapshotResponse>>, SnapshotPublishCommand>();

        // Inbound: the stand-in for the org consumer library, deleted at lift-and-shift.
        services.AddSingleton<IKafkaConsumerFactory, KafkaConsumerFactory>();
        services.AddHostedService<KafkaSnapshotConsumer>();

        // Outbound: the Application port, adapted onto the publish command.
        services.AddSingleton<IKafkaProducerFactory, KafkaProducerFactory>();
        services.AddSingleton<ISnapshotResponsePublisher, KafkaSnapshotResponsePublisher>();

        return services;
    }
}
