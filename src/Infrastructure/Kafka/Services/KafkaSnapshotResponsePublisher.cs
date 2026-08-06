using Ubs.Advantage.Core.Messaging.Kafka;
using Ubs.Advantage.Core.Messaging.Kafka.Models;
using UBS.Advantage.CommunicationModels.Snapshot;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Domain;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Services;

/// <summary>
/// Publishes the snapshot status notifications the write pipeline raises. It holds the mapping
/// only: the send and its outcome belong to the producer and to
/// <see cref="Commands.SnapshotResponseCommand"/>.
/// </summary>
public sealed class KafkaSnapshotResponsePublisher(IProducer<string, SnapshotResponse> producer) : ISnapshotResponsePublisher
{
    private readonly IProducer<string, SnapshotResponse> _producer = producer;

    public void Publish(SnapshotStatusNotification notification)
    {
        SnapshotResponse response = SnapshotResponseMapper.ToResponse(notification);
        Message<string, SnapshotResponse> payloadToPublish = new(Guid.NewGuid().ToString(), response, []);
        _producer.Publish(payloadToPublish);
    }
}
