using Microsoft.Extensions.DependencyInjection;
using Ubs.Advantage.Core.Messaging.Kafka.Extensions;
using UBS.Advantage.CommunicationModels.Snapshot;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Commands;
using UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Services;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka;

public static class KafkaRegistration
{
    public static IServiceCollection AddKafkaInfrastructure(
        this IServiceCollection services,
        bool enableDevelopmentLiveTesting)
    {
        services.AddTransient<ISnapshotResponsePublisher, KafkaSnapshotResponsePublisher>();
        services.AddMessageConsumerService<string, SnapshotRequest, SnapshotRequestCommand>("snapshot-request");
        services.AddMessageProducerService<string, SnapshotResponse, SnapshotResponseCommand>("snapshot-response");

        if (enableDevelopmentLiveTesting)
        {
            services.AddMessageProducerService<string, SnapshotRequest, LiveTestSnapshotRequestProducerCommand>("snapshot-request");
            services.AddMessageConsumerService<string, SnapshotResponse, LiveTestSnapshotResponseConsumerCommand>("snapshot-response");
        }

        return services;
    }
}
