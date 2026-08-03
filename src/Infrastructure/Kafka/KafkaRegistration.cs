using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;

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

        services.AddSingleton<IKafkaConsumerFactory, KafkaConsumerFactory>();
        services.AddHostedService<KafkaSnapshotConsumer>();

        services.AddSingleton<IKafkaProducerFactory, KafkaProducerFactory>();
        // Singleton: one long-lived producer per process, disposed with the host.
        services.AddSingleton<ISnapshotResponsePublisher, KafkaSnapshotResponsePublisher>();

        return services;
    }
}
